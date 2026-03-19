namespace Wolverine.Runtime.Metrics;

/// <summary>
/// Records that a message was received from an external transport. Posted when
/// <c>IMessageTracker.Received()</c> is called for non-local, non-stub transports.
/// Increments <see cref="PerTenantTracking.Received"/> by one. The <see cref="Source"/> field
/// carries the ServiceName of the receiving application for cross-service flow visualization.
/// </summary>
/// <param name="TenantId">The tenant identifier from the envelope.</param>
/// <param name="Source">The ServiceName of the application that received the message.</param>
public record RecordReceived(string TenantId, string Source) : IHandlerMetricsData
{
    /// <inheritdoc />
    public void Apply(PerTenantTracking tracking)
    {
        tracking.Received++;
    }
}
