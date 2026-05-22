using Microsoft.AspNetCore.SignalR;
using System.Threading.Tasks;

namespace QueryHeal.PoC;

public class QueryHealEventPublisher
{
    private readonly IHubContext<QueryHealHub> _hubContext;

    public QueryHealEventPublisher(IHubContext<QueryHealHub> hubContext)
    {
        _hubContext = hubContext;
    }

    public async Task BroadcastAnomalyAsync(string timestamp, string tableName, string suggestedFix, double wastedCpuMs, double wastedCarbonGrams, string callerInfo)
    {
        await _hubContext.Clients.All.SendAsync("ReceiveAnomaly", new
        {
            timestamp = timestamp,
            tableName = tableName,
            suggestedFix = suggestedFix,
            wastedCpuMs = wastedCpuMs,
            wastedCarbonGrams = wastedCarbonGrams,
            callerInfo = callerInfo
        });
    }
}