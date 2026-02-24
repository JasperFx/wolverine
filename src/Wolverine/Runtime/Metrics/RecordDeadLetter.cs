namespace Wolverine.Runtime.Metrics;

/// <summary>
/// Records that a message was moved to the dead-letter queue after exhausting all retry
/// policies. Posted when <c>MovedToErrorQueue</c> or <c>MessageFailed</c> is called.
/// Increments the dead-letter count for <see cref="ExceptionType"/> in
/// <see cref="PerTenantTracking.DeadLetterCounts"/>.
/// </summary>
/// <param name="ExceptionType">The fully-qualified CLR type name of the exception that caused the dead letter.</param>
/// <param name="TenantId">The tenant identifier from the envelope.</param>
public record RecordDeadLetter(string ExceptionType, string TenantId) : IHandlerMetricsData
{
    /// <inheritdoc />
    public void Apply(PerTenantTracking tracking)
    {
        if (!tracking.DeadLetterCounts.TryAdd(ExceptionType, 1))
        {
            tracking.DeadLetterCounts[ExceptionType] += 1;
        }
    }
}
