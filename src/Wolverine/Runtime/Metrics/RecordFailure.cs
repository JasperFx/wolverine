namespace Wolverine.Runtime.Metrics;

/// <summary>
/// Records that a message handler threw an exception during execution. Posted alongside
/// <see cref="RecordExecutionTime"/> when the handler faults. Increments the failure count
/// for <see cref="ExceptionType"/> in <see cref="PerTenantTracking.Failures"/>.
/// </summary>
/// <param name="ExceptionType">The fully-qualified CLR type name of the exception (e.g. "System.InvalidOperationException").</param>
/// <param name="TenantId">The tenant identifier from the envelope.</param>
public record RecordFailure(string ExceptionType, string TenantId) : IHandlerMetricsData
{
    /// <inheritdoc />
    public void Apply(PerTenantTracking tracking)
    {
        if (!tracking.Failures.TryAdd(ExceptionType, 1))
        {
            tracking.Failures[ExceptionType] += 1;
        }
    }
}
