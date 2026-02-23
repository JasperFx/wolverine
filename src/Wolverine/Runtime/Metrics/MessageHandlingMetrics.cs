using JasperFx.Core;

namespace Wolverine.Runtime.Metrics;

public record MessageHandlingMetrics(
    int NodeNumber,
    string MessageType,
    Uri Destination,
    TimeRange Range,
    PerTenantMetrics[] PerTenant)
{
    public static MessageHandlingMetrics Sum(string messageType, Uri destination, IReadOnlyList<MessageHandlingMetrics> metrics)
    {
        if (metrics.Count == 0)
        {
            return new MessageHandlingMetrics(0, messageType, destination, TimeRange.AllTime(), []);
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

        return new MessageHandlingMetrics(0, messageType, destination, range, perTenant);
    }
}

public record PerTenantMetrics(string TenantId, Executions Executions, EffectiveTime EffectiveTime, ExceptionCounts[] Exceptions)
{
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
}

public record Executions(int Count, long TotalTime);

public record EffectiveTime(int Count, double TotalTime);

public record ExceptionCounts(string ExceptionType, int Failures, int DeadLetters)
{
    public static ExceptionCounts Sum(IGrouping<string, ExceptionCounts> group)
    {
        return new ExceptionCounts(
            group.Key,
            group.Sum(e => e.Failures),
            group.Sum(e => e.DeadLetters));
    }
}
