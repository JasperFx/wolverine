using JasperFx.Core;

namespace Wolverine.Runtime.Metrics;

public record MessageHandlingMetrics(
    string MessageType,
    Uri Destination,
    TimeRange Range,
    PerTenantMetrics[] PerTenant // TODO -- we can remove this
)
{
    /// <summary>
    /// Used to group metrics down the line
    /// </summary>
    /// <returns></returns>
    public string Key() => $"{MessageType}@{Destination}";
    
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

    public static MessageHandlingMetrics Sum(MessageHandlingMetrics[] metrics)
    {
        return Sum(metrics[0].MessageType, metrics[0].Destination, metrics);
    }

    public static MessageHandlingMetrics[] SumByDestination(MessageHandlingMetrics[] metrics)
    {
        return metrics
            .GroupBy(m => m.Destination)
            .Select(g => Sum("*", g.Key, g.ToList()))
            .ToArray();
    }

    public static MessageHandlingMetrics[] SumByMessageType(MessageHandlingMetrics[] metrics)
    {
        var allDestination = new Uri("all://");
        return metrics
            .GroupBy(m => m.MessageType)
            .Select(g => Sum(g.Key, allDestination, g.ToList()))
            .ToArray();
    }

    public MessageHandlingMetrics Weight(int weight)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(weight);

        if (weight == 1) return this;

        return new MessageHandlingMetrics(MessageType, Destination, Range,
            PerTenant.Select(t => t.Weight(weight)).ToArray());
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

    public PerTenantMetrics Weight(int weight)
    {
        return new PerTenantMetrics(TenantId,
            Executions.Weight(weight),
            EffectiveTime.Weight(weight),
            Exceptions.Select(e => e.Weight(weight)).ToArray());
    }
}

public record Executions(int Count, long TotalTime)
{
    public Executions Weight(int weight) => new(Count * weight, TotalTime * weight);
}

public record EffectiveTime(int Count, double TotalTime)
{
    public EffectiveTime Weight(int weight) => new(Count * weight, TotalTime * weight);
}

public record ExceptionCounts(string ExceptionType, int Failures, int DeadLetters)
{
    public static ExceptionCounts Sum(IGrouping<string, ExceptionCounts> group)
    {
        return new ExceptionCounts(
            group.Key,
            group.Sum(e => e.Failures),
            group.Sum(e => e.DeadLetters));
    }

    public ExceptionCounts Weight(int weight) => new(ExceptionType, Failures * weight, DeadLetters * weight);
}
