namespace Wolverine.Persistence.Durability;

/// <summary>
/// Summarized count of dead letter messages
/// </summary>
/// <param name="ServiceName"></param>
/// <param name="ReceivedAt"></param>
/// <param name="MessageType"></param>
/// <param name="ExceptionType"></param>
/// <param name="TenantId"></param>
/// <param name="Count"></param>
public record DeadLetterQueueCount(string ServiceName, Uri ReceivedAt, string MessageType, string ExceptionType, string DatabaseIdentifier, int Count);

public record TimeRange(DateTimeOffset? From, DateTimeOffset? To)
{
    public static TimeRange AllTime() => new TimeRange(null, null);
}

public class DeadLetterEnvelopeQuery
{
    public DeadLetterEnvelopeQuery(TimeRange range)
    {
        Range = range;
    }

    public uint PageNumber { get; set; }
    public uint PageSize { get; set; } = 100;
    public string? MessageType { get; set; }
    public string? ExceptionType { get; set; }
    public string? ReceivedAt { get; set; }
    public string? DatabaseIdentifier { get; set; }
    
    public TimeRange Range { get; set; }
}

public class DeadLetterEnvelopeResults
{
    public int TotalCount { get; set; }
    public List<DeadLetterEnvelope> Envelopes { get; set; } = new();
}

public record MessageBatchRequest(Guid[] Ids, string DatabaseIdentifier);

/// <summary>
/// This is the dead letter service that is meant for CritterWatch usage
/// </summary>
public interface IDeadLetterAdminService
{
    Task<IReadOnlyList<DeadLetterQueueCount>> SummarizeAllAsync(string serviceName, TimeRange range,
        CancellationToken token);
    Task<IReadOnlyList<DeadLetterQueueCount>> SummarizeByDatabaseAsync(string serviceName, string databaseIdentifier,
        TimeRange range,
        CancellationToken token);

    Task<DeadLetterEnvelopeResults> QueryAsync(DeadLetterEnvelopeQuery query, CancellationToken token);

    Task DiscardAsync(DeadLetterEnvelopeQuery query, CancellationToken token);
    Task ReplayAsync(DeadLetterEnvelopeQuery query, CancellationToken token);

    Task DiscardAsync(MessageBatchRequest request, CancellationToken token);
    Task ReplayAsync(MessageBatchRequest request, CancellationToken token);

}