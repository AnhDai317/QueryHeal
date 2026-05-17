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

    public async Task BroadcastAnomalyAsync(string timestamp, string tableName, string suggestedFix, double wastedCpuMs, double wastedCarbonGrams)
    {
        await _hubContext.Clients.All.SendAsync("ReceiveAnomaly", new
        {
            Timestamp = timestamp,
            TableName = tableName,
            SuggestedFix = suggestedFix,
            WastedCpuMs = wastedCpuMs,
            WastedCarbonGrams = wastedCarbonGrams
        });
    }
}
