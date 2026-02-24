namespace Wolverine.Runtime.Metrics;

/// <summary>
/// Records the effective (end-to-end) time of a message from when it was sent to when processing
/// completed. Posted on both successful completion and dead-letter outcomes. Calculated as
/// <c>DateTimeOffset.UtcNow - envelope.SentAt</c> in milliseconds. Increments
/// <see cref="PerTenantTracking.Completions"/> by one and adds <see cref="Time"/> to
/// <see cref="PerTenantTracking.TotalEffectiveTime"/>.
/// </summary>
/// <param name="Time">The elapsed time in milliseconds from the envelope's <c>SentAt</c> timestamp to now.</param>
/// <param name="TenantId">The tenant identifier from the envelope.</param>
public record RecordEffectiveTime(double Time, string TenantId) : IHandlerMetricsData
{
    /// <inheritdoc />
    public void Apply(PerTenantTracking tracking)
    {
        tracking.Completions++;
        tracking.TotalEffectiveTime += Time;
    }
}
