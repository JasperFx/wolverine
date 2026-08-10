using JasperFx.Blocks;
using JasperFx.Core;
using Wolverine.Persistence.Durability.DeadLetterManagement;

namespace Wolverine.Runtime.Metrics;

/// <summary>
/// Accumulates handler metrics for a single message type and destination combination using
/// a batching pipeline. <see cref="IHandlerMetricsData"/> records are posted to <see cref="EntryPoint"/>
/// which batches them (up to 500 items or 250ms) before applying to the underlying
/// <see cref="MessageHandlingCounts"/>. On each sampling period, <see cref="TriggerExport(int, int)"/>
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
    /// after each <see cref="TriggerExport(int, int)"/> call.
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
    /// <see cref="MetricsAccumulator"/> on each sampling period. Uses the default idle-tenant
    /// eviction threshold of <see cref="MetricsOptions.DefaultTenantIdleEvictionCycles"/>.
    /// </summary>
    /// <param name="nodeNumber">The assigned node number for this Wolverine instance.</param>
    /// <returns>An immutable metrics snapshot for the completed accumulation window.</returns>
    public MessageHandlingMetrics TriggerExport(int nodeNumber)
    {
        return TriggerExport(nodeNumber, MetricsOptions.DefaultTenantIdleEvictionCycles);
    }

    /// <summary>
    /// Snapshots the accumulated counters into an immutable <see cref="MessageHandlingMetrics"/>
    /// record spanning from <see cref="Starting"/> to now, then resets the counters and advances
    /// <see cref="Starting"/> to the current time for the next accumulation window. Called by
    /// <see cref="MetricsAccumulator"/> on each sampling period.
    ///
    /// Only tenants with recorded activity in the window contribute a
    /// <see cref="PerTenantMetrics"/> entry — idle tenants emit nothing rather than an all-zero
    /// row. A tenant that stays idle for <paramref name="idleTenantEvictionCycles"/> consecutive
    /// exports is evicted from tracking entirely (and re-tracked automatically on its next
    /// activity), so the per-tenant series set stays bounded by recently-active tenants.
    /// </summary>
    /// <param name="nodeNumber">The assigned node number for this Wolverine instance.</param>
    /// <param name="idleTenantEvictionCycles">The number of consecutive idle export cycles after
    /// which a tenant's tracking entry is evicted. Zero or negative disables eviction.</param>
    /// <returns>An immutable metrics snapshot for the completed accumulation window. The
    /// <see cref="MessageHandlingMetrics.PerTenant"/> array is empty when no tenant recorded any
    /// activity in the window.</returns>
    public MessageHandlingMetrics TriggerExport(int nodeNumber, int idleTenantEvictionCycles)
    {
        lock (_syncLock)
        {
            var time = DateTimeOffset.UtcNow;

            var perTenant = new List<PerTenantMetrics>();
            List<string>? evictions = null;

            foreach (var tracking in Counts.PerTenant.OrderBy(x => x.TenantId))
            {
                if (tracking.HasActivity)
                {
                    tracking.ConsecutiveIdleExports = 0;
                    perTenant.Add(tracking.CompileAndReset());
                }
                else
                {
                    tracking.ConsecutiveIdleExports++;
                    if (idleTenantEvictionCycles > 0 && tracking.ConsecutiveIdleExports >= idleTenantEvictionCycles)
                    {
                        evictions ??= new List<string>();
                        evictions.Add(tracking.TenantId);
                    }
                }
            }

            if (evictions != null)
            {
                foreach (var tenantId in evictions)
                {
                    Counts.PerTenant.Remove(tenantId);
                }
            }

            var metrics = new MessageHandlingMetrics(MessageType,
                Destination,
                new TimeRange(Starting, time),
                perTenant.ToArray());

            Starting = time;

            return metrics;
        }
    }
}
