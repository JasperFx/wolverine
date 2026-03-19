namespace Wolverine.Runtime.Metrics;

/// <summary>
/// Records that a message was sent. Posted when <c>IMessageTracker.Sent()</c> is called.
/// Increments <see cref="PerTenantTracking.Sent"/> by one. The <see cref="Source"/> field
/// carries the ServiceName of the sending application for cross-service flow visualization.
/// </summary>
/// <param name="TenantId">The tenant identifier from the envelope.</param>
/// <param name="Source">The ServiceName of the application that sent the message.</param>
public record RecordSent(string TenantId, string Source) : IHandlerMetricsData
{
    /// <inheritdoc />
    public void Apply(PerTenantTracking tracking)
    {
        tracking.Sent++;
    }
}
