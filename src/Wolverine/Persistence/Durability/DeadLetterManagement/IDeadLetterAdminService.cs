namespace Wolverine.Persistence.Durability.DeadLetterManagement;

/// <summary>
/// This is the dead letter service that is meant for CritterWatch usage
/// </summary>
public interface IDeadLetterAdminService
{
    Task<IReadOnlyList<DeadLetterQueueCount>> SummarizeAllAsync(string serviceName, TimeRange range,
        CancellationToken token);
    Task<IReadOnlyList<DeadLetterQueueCount>> SummarizeByDatabaseAsync(string serviceName, Uri database,
        TimeRange range,
        CancellationToken token);

    Task<DeadLetterEnvelopeResults> QueryAsync(DeadLetterEnvelopeQuery query, CancellationToken token);

    Task DiscardAsync(DeadLetterEnvelopeQuery query, CancellationToken token);
    Task ReplayAsync(DeadLetterEnvelopeQuery query, CancellationToken token);

    Task DiscardAsync(MessageBatchRequest request, CancellationToken token);
    Task ReplayAsync(MessageBatchRequest request, CancellationToken token);

}