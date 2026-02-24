using JasperFx.Core;

namespace Wolverine.Runtime.Metrics;

/// <summary>
/// Immutable snapshot of message handling metrics for a specific message type and destination
/// combination over a time range. Produced by <see cref="MessageTypeMetricsAccumulator.TriggerExport"/>
/// on each sampling period. Contains per-tenant breakdowns of execution counts, effective times,
/// and exception counts.
/// </summary>
/// <param name="MessageType">The fully-qualified CLR message type name. Set to <c>"*"</c> in
/// <see cref="SumByDestination"/> results to indicate aggregation across all message types.</param>
/// <param name="Destination">The destination endpoint URI. Set to <c>all://</c> in
/// <see cref="SumByMessageType"/> results to indicate aggregation across all destinations.</param>
/// <param name="Range">The time window this snapshot covers, from the start of accumulation to the export timestamp.</param>
/// <param name="PerTenant">Per-tenant metrics breakdowns. Empty when no messages were processed in the time range.</param>
public record MessageHandlingMetrics(
    string MessageType,
    Uri Destination,
    TimeRange Range,
    PerTenantMetrics[] PerTenant // TODO -- we can remove this
)
{
    /// <summary>
    /// Returns a grouping key in the format <c>"MessageType@Destination"</c> for use in
    /// downstream aggregation and lookup scenarios.
    /// </summary>
    /// <returns>A composite key string.</returns>
    public string Key() => $"{MessageType}@{Destination}";

    /// <summary>
    /// Combines multiple <see cref="MessageHandlingMetrics"/> snapshots into a single aggregate.
    /// The time range spans the earliest <c>From</c> to the latest <c>To</c> across all inputs.
    /// Per-tenant data is grouped by tenant ID, with execution counts, effective times, and
    /// exception counts summed within each tenant.
    /// </summary>
    /// <param name="messageType">The message type to assign to the resulting aggregate.</param>
    /// <param name="destination">The destination to assign to the resulting aggregate.</param>
    /// <param name="metrics">The metrics snapshots to aggregate.</param>
    /// <returns>A single aggregated snapshot. Returns an empty all-time snapshot if the input is empty.</returns>
    public static MessageHandlingMetrics Sum(string messageType, Uri destination, IReadOnlyList<MessageHandlingMetrics> metrics)
    {
        if (metrics.Count == 0)
        {
            return new MessageHandlingMetrics(messageType, destination, TimeRange.AllTime(), []);
        }

        var from = metrics.Where(m => m.Range.From.HasValue).Select(m => m.Range.From!.Value).DefaultIfEmpty().Min();
        var to = metrics.Where(m => m.Range.To.HasValue).Select(m => m.Range.To!.Value).DefaultIfEmpty().Max();
        var range = new TimeRange(
            metrics.Any(m => m.Range.From.HasValue) ? from : null,
            metrics.Any(m => m.Range.To.HasValue) ? to : null);

        var perTenant = metrics
            .SelectMany(m => m.PerTenant)
            .GroupBy(t => t.TenantId)
            .Select(PerTenantMetrics.Sum)
            .ToArray();

        return new MessageHandlingMetrics(messageType, destination, range, perTenant);
    }

    /// <summary>
    /// Combines multiple <see cref="MessageHandlingMetrics"/> snapshots that share the same
    /// message type and destination. Takes the message type and destination from the first element.
    /// </summary>
    /// <param name="metrics">The metrics snapshots to aggregate. Must contain at least one element.</param>
    /// <returns>A single aggregated snapshot.</returns>
    public static MessageHandlingMetrics Sum(MessageHandlingMetrics[] metrics)
    {
        return Sum(metrics[0].MessageType, metrics[0].Destination, metrics);
    }

    /// <summary>
    /// Groups the input metrics by <see cref="Destination"/> and sums each group, collating
    /// across message types. Each resulting snapshot has its <see cref="MessageType"/> set to
    /// <c>"*"</c> to indicate aggregation across all message types for that destination.
    /// </summary>
    /// <param name="metrics">The metrics snapshots to group and aggregate.</param>
    /// <returns>One aggregated snapshot per unique destination URI.</returns>
    public static MessageHandlingMetrics[] SumByDestination(MessageHandlingMetrics[] metrics)
    {
        return metrics
            .GroupBy(m => m.Destination)
            .Select(g => Sum("*", g.Key, g.ToList()))
            .ToArray();
    }

    /// <summary>
    /// Groups the input metrics by <see cref="MessageType"/> and sums each group, collating
    /// across destinations. Each resulting snapshot has its <see cref="Destination"/> set to
    /// <c>all://</c> to indicate aggregation across all destinations for that message type.
    /// </summary>
    /// <param name="metrics">The metrics snapshots to group and aggregate.</param>
    /// <returns>One aggregated snapshot per unique message type.</returns>
    public static MessageHandlingMetrics[] SumByMessageType(MessageHandlingMetrics[] metrics)
    {
        var allDestination = new Uri("all://");
        return metrics
            .GroupBy(m => m.MessageType)
            .Select(g => Sum(g.Key, allDestination, g.ToList()))
            .ToArray();
    }

    /// <summary>
    /// Multiplies every numeric value in this snapshot and all nested records by the given
    /// <paramref name="weight"/>. Returns <c>this</c> unchanged when <paramref name="weight"/>
    /// is 1. Intended for building weighted average calculations where snapshots from different
    /// time periods or nodes need proportional scaling before summation.
    /// </summary>
    /// <param name="weight">The multiplier to apply. Must be a positive integer.</param>
    /// <returns>The same instance if weight is 1, otherwise a new weighted copy.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="weight"/> is zero or negative.</exception>
    public MessageHandlingMetrics Weight(int weight)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(weight);

        if (weight == 1) return this;

        return new MessageHandlingMetrics(MessageType, Destination, Range,
            PerTenant.Select(t => t.Weight(weight)).ToArray());
    }
}

/// <summary>
/// Immutable snapshot of metrics for a single tenant within a <see cref="MessageHandlingMetrics"/>
/// record. Compiled from <see cref="PerTenantTracking.CompileAndReset"/> during each export cycle.
/// Contains execution statistics, effective time statistics, and per-exception-type error counts.
/// </summary>
/// <param name="TenantId">The tenant identifier. Defaults to <c>StorageConstants.DefaultTenantId</c> for
/// messages without an explicit tenant.</param>
/// <param name="Executions">Handler execution count and total execution time in milliseconds.</param>
/// <param name="EffectiveTime">Message completion count and total end-to-end time in milliseconds.</param>
/// <param name="Exceptions">Per-exception-type counts of failures and dead letters, ordered alphabetically by type name.</param>
public record PerTenantMetrics(string TenantId, Executions Executions, EffectiveTime EffectiveTime, ExceptionCounts[] Exceptions)
{
    /// <summary>
    /// Sums a group of <see cref="PerTenantMetrics"/> records sharing the same tenant ID.
    /// Execution counts and times are summed directly. Exception counts are grouped by
    /// exception type and summed via <see cref="ExceptionCounts.Sum"/>.
    /// </summary>
    /// <param name="group">A grouping of per-tenant metrics keyed by tenant ID.</param>
    /// <returns>A single aggregated per-tenant snapshot.</returns>
    public static PerTenantMetrics Sum(IGrouping<string, PerTenantMetrics> group)
    {
        var tenantId = group.Key;
        var executions = new Executions(
            group.Sum(t => t.Executions.Count),
            group.Sum(t => t.Executions.TotalTime));
        var effectiveTime = new EffectiveTime(
            group.Sum(t => t.EffectiveTime.Count),
            group.Sum(t => t.EffectiveTime.TotalTime));
        var exceptions = group
            .SelectMany(t => t.Exceptions)
            .GroupBy(e => e.ExceptionType)
            .Select(ExceptionCounts.Sum)
            .ToArray();

        return new PerTenantMetrics(tenantId, executions, effectiveTime, exceptions);
    }

    /// <summary>
    /// Multiplies all numeric values (execution counts and times, effective time counts and times,
    /// and all exception failure/dead-letter counts) by the given weight.
    /// </summary>
    /// <param name="weight">The multiplier to apply.</param>
    /// <returns>A new weighted copy of this per-tenant snapshot.</returns>
    public PerTenantMetrics Weight(int weight)
    {
        return new PerTenantMetrics(TenantId,
            Executions.Weight(weight),
            EffectiveTime.Weight(weight),
            Exceptions.Select(e => e.Weight(weight)).ToArray());
    }
}

/// <summary>
/// Handler execution statistics: the number of executions and total wall-clock execution time.
/// Accumulated from <see cref="RecordExecutionTime"/> data points where each handler invocation
/// increments <see cref="Count"/> by one and adds its duration to <see cref="TotalTime"/>.
/// Average execution time can be calculated as <c>TotalTime / Count</c>.
/// </summary>
/// <param name="Count">The number of handler executions.</param>
/// <param name="TotalTime">The sum of handler execution durations in milliseconds.</param>
public record Executions(int Count, long TotalTime)
{
    /// <summary>
    /// Multiplies both <see cref="Count"/> and <see cref="TotalTime"/> by the given weight.
    /// </summary>
    /// <param name="weight">The multiplier to apply.</param>
    /// <returns>A new weighted copy.</returns>
    public Executions Weight(int weight) => new(Count * weight, TotalTime * weight);
}

/// <summary>
/// End-to-end (effective) time statistics: the number of completed messages and their total
/// elapsed time from send to completion. Accumulated from <see cref="RecordEffectiveTime"/>
/// data points where each completion increments <see cref="Count"/> by one and adds its
/// elapsed time to <see cref="TotalTime"/>. This measures latency from <c>Envelope.SentAt</c>
/// to processing completion, capturing queueing, transport, and handler execution time combined.
/// Average effective time can be calculated as <c>TotalTime / Count</c>.
/// </summary>
/// <param name="Count">The number of messages that completed processing.</param>
/// <param name="TotalTime">The sum of end-to-end elapsed times in milliseconds.</param>
public record EffectiveTime(int Count, double TotalTime)
{
    /// <summary>
    /// Multiplies both <see cref="Count"/> and <see cref="TotalTime"/> by the given weight.
    /// </summary>
    /// <param name="weight">The multiplier to apply.</param>
    /// <returns>A new weighted copy.</returns>
    public EffectiveTime Weight(int weight) => new(Count * weight, TotalTime * weight);
}

/// <summary>
/// Per-exception-type error counts within a tenant. Tracks both transient failures
/// (from <see cref="RecordFailure"/>) where the message may be retried, and dead letters
/// (from <see cref="RecordDeadLetter"/>) where the message was moved to the error queue
/// after exhausting retry policies.
/// </summary>
/// <param name="ExceptionType">The fully-qualified CLR exception type name (e.g. "System.InvalidOperationException").</param>
/// <param name="Failures">The number of handler failures (exceptions thrown) for this exception type.</param>
/// <param name="DeadLetters">The number of messages dead-lettered due to this exception type.</param>
public record ExceptionCounts(string ExceptionType, int Failures, int DeadLetters)
{
    /// <summary>
    /// Sums a group of <see cref="ExceptionCounts"/> records sharing the same exception type.
    /// </summary>
    /// <param name="group">A grouping of exception counts keyed by exception type name.</param>
    /// <returns>A single aggregated exception count.</returns>
    public static ExceptionCounts Sum(IGrouping<string, ExceptionCounts> group)
    {
        return new ExceptionCounts(
            group.Key,
            group.Sum(e => e.Failures),
            group.Sum(e => e.DeadLetters));
    }

    /// <summary>
    /// Multiplies both <see cref="Failures"/> and <see cref="DeadLetters"/> by the given weight.
    /// </summary>
    /// <param name="weight">The multiplier to apply.</param>
    /// <returns>A new weighted copy.</returns>
    public ExceptionCounts Weight(int weight) => new(ExceptionType, Failures * weight, DeadLetters * weight);
}
