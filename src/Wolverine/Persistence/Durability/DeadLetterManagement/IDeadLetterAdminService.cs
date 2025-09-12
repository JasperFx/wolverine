namespace Wolverine.Persistence.Durability.DeadLetterManagement;

/// <summary>
/// This is the dead letter service that is meant for CritterWatch usage
/// </summary>
public interface IDeadLetterAdminService
{
    Task<IReadOnlyList<DeadLetterQueueCount>> SummarizeAllAsync(string serviceName, TimeRange range,
        CancellationToken token);

    Task<DeadLetterEnvelopeResults> QueryAsync(DeadLetterEnvelopeQuery query, CancellationToken token);

    Task DiscardAsync(DeadLetterEnvelopeQuery query, CancellationToken token);
    Task ReplayAsync(DeadLetterEnvelopeQuery query, CancellationToken token);

    Uri Uri { get; }
}