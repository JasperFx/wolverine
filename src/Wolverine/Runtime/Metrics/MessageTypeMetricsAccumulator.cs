using JasperFx.Blocks;
using JasperFx.Core;
using Wolverine.Persistence.Durability.DeadLetterManagement;

namespace Wolverine.Runtime.Metrics;

public class MessageTypeMetricsAccumulator
{
    private readonly object _syncLock = new();
    
    public string MessageType { get; }
    public Uri Destination { get; }

    public MessageTypeMetricsAccumulator(string messageType, Uri destination)
    {
        MessageType = messageType;
        Destination = destination;

        Counts = new MessageHandlingCounts(messageType, destination);
        var processor = new Block<IHandlerMetricsData[]>(Process);
        EntryPoint = processor.BatchUpstream(250.Milliseconds(), 500);
    }
    
    public DateTimeOffset Starting { get; private set; } = DateTimeOffset.UtcNow;

    public MessageHandlingCounts Counts { get; }

    public IBlock<IHandlerMetricsData> EntryPoint { get; }

    public void Process(IHandlerMetricsData[] instruments)
    {
        lock (_syncLock)
        {
            foreach (var instrument in instruments)
            {
                try
                {
                    Counts.Increment(instrument);
                }
                catch (Exception )
                {
                    // for now
                }
            }
        }
    }

    public MessageHandlingMetrics TriggerExport(int nodeNumber)
    {
        lock (_syncLock)
        {
            var time = DateTimeOffset.UtcNow;
            
            var metrics = new MessageHandlingMetrics(
                nodeNumber,
                MessageType, 
                Destination,
                new TimeRange(Starting, time),
                Counts.PerTenant.OrderBy(x => x.TenantId).Select(x => x.CompileAndReset()).ToArray());

            Starting = time;

            return metrics;
        }
    }
}