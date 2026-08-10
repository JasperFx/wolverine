namespace Wolverine.Runtime.Metrics;

/// <summary>
/// Mutable, in-process counters that accumulate raw metrics data for a single tenant within
/// a <see cref="MessageHandlingCounts"/> instance. <see cref="IHandlerMetricsData"/> records
/// mutate these counters via <see cref="IHandlerMetricsData.Apply"/>. On each sampling period
/// export, <see cref="CompileAndReset"/> snapshots the counters into an immutable
/// <see cref="PerTenantMetrics"/> record and resets all values to zero.
/// </summary>
public class PerTenantTracking
{
    /// <summary>
    /// The tenant identifier this tracking instance accumulates data for.
    /// </summary>
    public string TenantId { get; }

    /// <summary>
    /// Creates a new per-tenant tracking instance.
    /// </summary>
    /// <param name="tenantId">The tenant identifier.</param>
    public PerTenantTracking(string tenantId)
    {
        TenantId = tenantId;
    }

    /// <summary>
    /// The number of handler executions recorded. Incremented by <see cref="RecordExecutionTime"/>.
    /// </summary>
    public int Executions { get; set; }

    /// <summary>
    /// The sum of handler execution durations in milliseconds. Incremented by <see cref="RecordExecutionTime"/>.
    /// </summary>
    public long TotalExecutionTime { get; set; }

    /// <summary>
    /// The number of messages that completed processing (success or dead-letter).
    /// Incremented by <see cref="RecordEffectiveTime"/>.
    /// </summary>
    public int Completions { get; set; }

    /// <summary>
    /// The sum of effective (end-to-end) times in milliseconds, measured from envelope
    /// <c>SentAt</c> to processing completion. Incremented by <see cref="RecordEffectiveTime"/>.
    /// </summary>
    public double TotalEffectiveTime { get; set; }

    /// <summary>
    /// The number of messages sent. Incremented by <see cref="RecordSent"/>.
    /// </summary>
    public int Sent { get; set; }

    /// <summary>
    /// The number of messages received from external transports. Incremented by <see cref="RecordReceived"/>.
    /// </summary>
    public int Received { get; set; }

    /// <summary>
    /// Dead-letter counts keyed by fully-qualified exception type name. Incremented
    /// by <see cref="RecordDeadLetter"/>.
    /// </summary>
    public Dictionary<string, int> DeadLetterCounts { get; } = new();

    /// <summary>
    /// Failure counts keyed by fully-qualified exception type name. Incremented
    /// by <see cref="RecordFailure"/>.
    /// </summary>
    public Dictionary<string, int> Failures { get; } = new();

    /// <summary>
    /// The number of consecutive export cycles in which this tenant recorded no activity at all.
    /// Maintained by <see cref="MessageTypeMetricsAccumulator.TriggerExport(int, int)"/> — reset
    /// to zero whenever the tenant has activity, incremented on each idle export. When it reaches
    /// the configured eviction threshold (<c>WolverineOptions.Metrics.TenantIdleEvictionCycles</c>),
    /// the tenant entry is removed from tracking altogether and will be re-created on its next
    /// recorded activity.
    /// </summary>
    public int ConsecutiveIdleExports { get; set; }

    /// <summary>
    /// Whether any activity at all has been recorded in the current accumulation window — any
    /// non-zero counter or duration, or any failure or dead-letter entry. A tenant with no
    /// activity contributes nothing to the exported snapshot.
    /// </summary>
    public bool HasActivity => Executions != 0
                               || TotalExecutionTime != 0
                               || Completions != 0
                               || TotalEffectiveTime != 0
                               || Sent != 0
                               || Received != 0
                               || DeadLetterCounts.Count > 0
                               || Failures.Count > 0;

    /// <summary>
    /// Snapshots the current counter values into an immutable <see cref="PerTenantMetrics"/>
    /// record, then resets all counters to zero. The exception counts are compiled by taking
    /// the union of exception types across <see cref="Failures"/> and <see cref="DeadLetterCounts"/>.
    /// </summary>
    /// <returns>An immutable snapshot of the accumulated metrics for this tenant.</returns>
    public PerTenantMetrics CompileAndReset()
    {
        var exceptionTypes = DeadLetterCounts.Keys.Union(Failures.Keys).ToArray();

        var response = new PerTenantMetrics(
            TenantId,
            new Executions(Executions, TotalExecutionTime),
            new EffectiveTime(Completions, TotalEffectiveTime),
            exceptionTypes.OrderBy(x => x).Select(exceptionType =>
            {
                int failures = 0;
                int deadLetters = 0;
                DeadLetterCounts.TryGetValue(exceptionType, out deadLetters);
                Failures.TryGetValue(exceptionType, out failures);

                return new ExceptionCounts(exceptionType, failures, deadLetters);
            }).ToArray(),
            Sent,
            Received
        );

        Clear();

        return response;
    }

    /// <summary>
    /// Resets all counters and dictionaries to their initial zero state.
    /// </summary>
    public void Clear()
    {
        Executions = 0;
        TotalExecutionTime = 0;
        Completions = 0;
        TotalEffectiveTime = 0;
        Sent = 0;
        Received = 0;
        DeadLetterCounts.Clear();
        Failures.Clear();
    }

}
