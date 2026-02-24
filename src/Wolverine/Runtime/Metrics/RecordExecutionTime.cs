namespace Wolverine.Runtime.Metrics;

/// <summary>
/// Records the wall-clock execution time of a single message handler invocation. Posted when
/// <c>Envelope.StopTiming()</c> returns a positive value after handler execution completes.
/// Increments <see cref="PerTenantTracking.Executions"/> by one and adds <see cref="Time"/>
/// to <see cref="PerTenantTracking.TotalExecutionTime"/>.
/// </summary>
/// <param name="Time">The handler execution duration in milliseconds, from <c>Envelope.StopTiming()</c>.</param>
/// <param name="TenantId">The tenant identifier from the envelope.</param>
public record RecordExecutionTime(long Time, string TenantId) : IHandlerMetricsData
{
    /// <inheritdoc />
    public void Apply(PerTenantTracking tracking)
    {
        tracking.Executions++;
        tracking.TotalExecutionTime += Time;
    }
}
