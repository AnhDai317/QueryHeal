using System;
using System.Collections.Concurrent;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore.Diagnostics;
namespace QueryHeal.PoC;

public class QueryHealInterceptor : DbCommandInterceptor
{
    private readonly ConcurrentDictionary<string, QueryTracker> _trackers = new();
    private readonly QueryHealEventPublisher _publisher;

    public QueryHealInterceptor(QueryHealEventPublisher publisher)
    {
        _publisher = publisher;
    }

    // Configurable thresholds for N+1 anomaly detection
    private const int NPlusOneThreshold = 5;
    private static readonly TimeSpan TimeWindow = TimeSpan.FromMilliseconds(100);

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        TrackCommand(command);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result)
    {
        TrackCommand(command);
        return base.ReaderExecuting(command, eventData, result);
    }

    private void TrackCommand(DbCommand command)
    {
        var sql = command.CommandText;
        if (string.IsNullOrWhiteSpace(sql)) return;

        // Avoid string allocation on the hot path (e.g. no TrimStart())
        var span = sql.AsSpan().TrimStart();
        if (!span.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)) return;

        // Use Environment.TickCount64 for high performance, low-allocation time tracking
        var now = Environment.TickCount64;
        _trackers.AddOrUpdate(sql,
            _ => new QueryTracker(now),
            (_, tracker) =>
            {
                lock (tracker)
                {
                    if (now - tracker.LastSeenAt > TimeWindow.TotalMilliseconds)
                    {
                        // Reset the window if the gap since the last query is larger than our threshold
                        tracker.FirstSeenAt = now;
                        tracker.Count = 1;
                        tracker.Reported = false;
                    }
                    else
                    {
                        tracker.Count++;
                        if (tracker.Count >= NPlusOneThreshold && !tracker.Reported)
                        {
                            tracker.Reported = true;
                            ReportAnomaly(sql, tracker.Count, TimeWindow.TotalMilliseconds, GetCallerInfo());
                        }
                    }

                    tracker.LastSeenAt = now;
                }
                return tracker;
            });
    }

    private static readonly Regex TableRegex = new(@"FROM\s+[""\[`]?([a-zA-Z0-9_]+)[""\]`]?", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private void ReportAnomaly(string sql, int count, double timeWindowMs, string callerInfo)
    {
        // Heuristics: 1 redundant query = 0.05g CO2, 5ms CPU time wasted
        double co2PerQuery = 0.05;
        double cpuTimePerQueryMs = 5.0;

        int redundantQueries = count - 1; // 1 is normal, the rest are redundant
        double totalCo2 = redundantQueries * co2PerQuery;
        double totalCpuTime = redundantQueries * cpuTimePerQueryMs;

        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine("\n========================================================");
        Console.WriteLine($"[QueryHeal] N+1 ANOMALY DETECTED!");
        Console.WriteLine("========================================================");
        Console.ResetColor();

        PrintSmartSuggestion(sql, totalCpuTime, totalCo2, callerInfo);

        Console.WriteLine("--------------------------------------------------------");
        Console.WriteLine($"Query Signature:");
        Console.WriteLine(sql.Trim());
        Console.WriteLine("========================================================\n");
    }

    private void PrintSmartSuggestion(string sql, double totalCpuTime, double totalCo2, string callerInfo)
    {
        string tableName = ExtractTableName(sql);
        string propertyName = GetNavigationPropertyName(tableName);

        Console.WriteLine("========================================================");
        Console.WriteLine("\x1b[31m[QueryHeal] SMART SUGGESTION:\x1b[0m");
        Console.WriteLine("========================================================");
        Console.WriteLine($"\x1b[31m Problem: N+1 Query detected on '{tableName}'.\x1b[0m");
        Console.WriteLine($"\x1b[33m↳ Triggered at: {callerInfo}\x1b[0m");
        Console.WriteLine("\x1b[36m Fix: Add .Include() to your LINQ query:\x1b[0m");
        Console.WriteLine("\x1b[37m   BEFORE: var data = query.ToList();\x1b[0m");
        Console.WriteLine($"\x1b[32m   AFTER:  var data = query.Include(x => x.{propertyName}).ToList();\x1b[0m");
        Console.WriteLine($"\x1b[92m Impact: Saved ~{totalCpuTime:F0}ms CPU = {totalCo2:F2}g CO2e per execution.\x1b[0m");
        Console.WriteLine("========================================================");

        // Broadcast to SignalR Web Dashboard
        var fixText = $"var data = query.Include(x => x.{propertyName}).ToList();";
        var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");

        // Fire and forget so we don't block the interception
        _ = _publisher.BroadcastAnomalyAsync(timestamp, tableName, fixText, totalCpuTime, totalCo2, callerInfo);
    }

    private static string ExtractTableName(string sql)
    {
        try
        {
            var match = TableRegex.Match(sql);
            if (match.Success && match.Groups.Count > 1)
            {
                return match.Groups[1].Value;
            }
        }
        catch
        {
            // Suppress exception to ensure <1ms latency and no crashes on the hot path
        }
        return "UnknownTable";
    }

    private static string GetNavigationPropertyName(string tableName)
    {
        if (string.IsNullOrWhiteSpace(tableName)) return "Property";

        // Basic plural-to-singular conversion for the PoC
        if (tableName.EndsWith("ies", StringComparison.OrdinalIgnoreCase))
        {
            return tableName.Substring(0, tableName.Length - 3) + "y";
        }
        if (tableName.EndsWith("s", StringComparison.OrdinalIgnoreCase))
        {
            return tableName.Substring(0, tableName.Length - 1);
        }

        return tableName;
    }

    private static string GetCallerInfo()
    {
        try
        {
            var stackTrace = new System.Diagnostics.StackTrace(true);
            foreach (var frame in stackTrace.GetFrames())
            {
                var method = frame.GetMethod();
                var declaringType = method?.DeclaringType;
                if (declaringType != null && 
                    !declaringType.FullName.StartsWith("System.") && 
                    !declaringType.FullName.StartsWith("Microsoft.") &&
                    declaringType.Name != "QueryHealInterceptor")
                {
                    var file = frame.GetFileName();
                    if (!string.IsNullOrEmpty(file))
                    {
                        var line = frame.GetFileLineNumber();
                        return $"{System.IO.Path.GetFileName(file)}:line {line}";
                    }
                }
            }
        }
        catch { }
        return "Unknown caller";
    }

    private class QueryTracker
    {
        public long FirstSeenAt { get; set; }
        public long LastSeenAt { get; set; }
        public int Count { get; set; }
        public bool Reported { get; set; }

        public QueryTracker(long firstSeenAt)
        {
            FirstSeenAt = firstSeenAt;
            LastSeenAt = firstSeenAt;
            Count = 1;
            Reported = false;
        }
    }
}
