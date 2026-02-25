using JasperFx;
using JasperFx.Core;

namespace Wolverine.Runtime.Metrics;

/// <summary>
/// Maintains a set of <see cref="PerTenantTracking"/> instances for a specific message type
/// and destination combination. When an <see cref="IHandlerMetricsData"/> record arrives,
/// <see cref="Increment"/> resolves the appropriate tenant tracker (creating one on first access)
/// and delegates to <see cref="IHandlerMetricsData.Apply"/>. Null tenant IDs are mapped to
/// <c>StorageConstants.DefaultTenantId</c>.
/// </summary>
public class MessageHandlingCounts
{
    /// <summary>
    /// The fully-qualified CLR message type name this instance tracks.
    /// </summary>
    public string MessageType { get; }

    /// <summary>
    /// The destination endpoint URI this instance tracks.
    /// </summary>
    public Uri Destination { get; }

    /// <summary>
    /// Creates a new counts tracker for a specific message type and destination.
    /// </summary>
    /// <param name="messageType">The fully-qualified CLR message type name.</param>
    /// <param name="destination">The destination endpoint URI.</param>
    public MessageHandlingCounts(string messageType, Uri destination)
    {
        MessageType = messageType;
        Destination = destination;
    }

    /// <summary>
    /// Lazily-populated cache of <see cref="PerTenantTracking"/> instances keyed by tenant ID.
    /// New tenants are automatically created on first access.
    /// </summary>
    public LightweightCache<string, PerTenantTracking> PerTenant { get; } =
        new(tenantId => new PerTenantTracking(tenantId));

    /// <summary>
    /// Routes a metrics data point to the correct per-tenant tracker and applies it.
    /// </summary>
    /// <param name="metricsData">The metrics data point to accumulate.</param>
    public void Increment(IHandlerMetricsData metricsData)
    {
        var perTenant = PerTenant[metricsData.TenantId ?? StorageConstants.DefaultTenantId];
        metricsData.Apply(perTenant);
    }

    /// <summary>
    /// Clears all per-tenant counters without removing the tenant entries.
    /// </summary>
    public void Clear()
    {
        foreach (var tracking in PerTenant)
        {
            tracking.Clear();
        }

    }
}
