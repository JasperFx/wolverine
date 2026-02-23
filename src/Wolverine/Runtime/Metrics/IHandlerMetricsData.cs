namespace Wolverine.Runtime.Metrics;

/// <summary>
/// Represents a single metrics data point captured during message handling. Implementations
/// are posted to the <see cref="MessageTypeMetricsAccumulator"/> batching pipeline where they
/// are applied to the appropriate <see cref="PerTenantTracking"/> counters.
/// </summary>
public interface IHandlerMetricsData
{
    /// <summary>
    /// The tenant identifier for multi-tenant routing. When null, the data point
    /// is assigned to <c>StorageConstants.DefaultTenantId</c> during accumulation.
    /// </summary>
    string TenantId { get; }

    /// <summary>
    /// Applies this data point to the mutable per-tenant tracking counters.
    /// Called under a lock within <see cref="MessageTypeMetricsAccumulator.Process"/>.
    /// </summary>
    /// <param name="tracking">The per-tenant tracking instance whose counters will be mutated.</param>
    void Apply(PerTenantTracking tracking);
}
