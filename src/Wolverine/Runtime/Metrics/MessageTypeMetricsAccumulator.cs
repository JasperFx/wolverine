using JasperFx.Blocks;
using JasperFx.Core;
using Wolverine.Persistence.Durability.DeadLetterManagement;

namespace Wolverine.Runtime.Metrics;

/// <summary>
/// Accumulates handler metrics for a single message type and destination combination using
/// a batching pipeline. <see cref="IHandlerMetricsData"/> records are posted to <see cref="EntryPoint"/>
/// which batches them (up to 500 items or 250ms) before applying to the underlying
/// <see cref="MessageHandlingCounts"/>. On each sampling period, <see cref="TriggerExport"/>
/// snapshots the accumulated counters into an immutable <see cref="MessageHandlingMetrics"/>
/// record and resets the counters for the next period.
/// </summary>
public class MessageTypeMetricsAccumulator
{
    private readonly object _syncLock = new();

    /// <summary>
    /// The fully-qualified CLR message type name being tracked.
    /// </summary>
    public string MessageType { get; }

    /// <summary>
    /// The destination endpoint URI being tracked.
    /// </summary>
    public Uri Destination { get; }

    /// <summary>
    /// Creates a new accumulator for a specific message type and destination. Initializes the
    /// batching pipeline that feeds into <see cref="Process"/>.
    /// </summary>
    /// <param name="messageType">The fully-qualified CLR message type name.</param>
    /// <param name="destination">The destination endpoint URI.</param>
    public MessageTypeMetricsAccumulator(string messageType, Uri destination)
    {
        MessageType = messageType;
        Destination = destination;

        Counts = new MessageHandlingCounts(messageType, destination);
        var processor = new Block<IHandlerMetricsData[]>(Process);
        EntryPoint = processor.BatchUpstream(250.Milliseconds(), 500);
    }

    /// <summary>
    /// The start of the current accumulation time window. Reset to <c>DateTimeOffset.UtcNow</c>
    /// after each <see cref="TriggerExport"/> call.
    /// </summary>
    public DateTimeOffset Starting { get; private set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// The underlying mutable counter storage for this message type and destination.
    /// </summary>
    public MessageHandlingCounts Counts { get; }

    /// <summary>
    /// The entry point for the batching pipeline. Post <see cref="IHandlerMetricsData"/> records
    /// here; they will be batched and forwarded to <see cref="Process"/> for accumulation.
    /// </summary>
    public IBlock<IHandlerMetricsData> EntryPoint { get; }

    /// <summary>
    /// Processes a batch of metrics data points by applying each to the underlying
    /// <see cref="Counts"/> under a lock. Called by the batching pipeline.
    /// </summary>
    /// <param name="instruments">A batch of metrics data points to accumulate.</param>
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

    /// <summary>
    /// Snapshots the accumulated counters into an immutable <see cref="MessageHandlingMetrics"/>
    /// record spanning from <see cref="Starting"/> to now, then resets the counters and advances
    /// <see cref="Starting"/> to the current time for the next accumulation window. Called by
    /// <see cref="MetricsAccumulator"/> on each sampling period.
    /// </summary>
    /// <param name="nodeNumber">The assigned node number for this Wolverine instance.</param>
    /// <returns>An immutable metrics snapshot for the completed accumulation window.</returns>
    public MessageHandlingMetrics TriggerExport(int nodeNumber)
    {
        lock (_syncLock)
        {
            var time = DateTimeOffset.UtcNow;

            var metrics = new MessageHandlingMetrics(MessageType,
                Destination,
                new TimeRange(Starting, time),
                Counts.PerTenant.OrderBy(x => x.TenantId).Select(x => x.CompileAndReset()).ToArray());

            Starting = time;

            return metrics;
        }
    }
}
