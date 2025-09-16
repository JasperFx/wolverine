using Wolverine.Persistence.Durability.DeadLetterManagement;

namespace Wolverine.Persistence.Durability;

/// <summary>
/// This is the original V2/3 service for dead letter querying
/// </summary>
public interface IDeadLetters
{
    Task<DeadLetterEnvelopesFound> QueryDeadLetterEnvelopesAsync(DeadLetterEnvelopeQueryParameters queryParameters, string? tenantId = null);

    /// <param name="tenantId">Leaving tenantId null will query all tenants</param>
    Task<DeadLetterEnvelope?> DeadLetterEnvelopeByIdAsync(Guid id, string? tenantId = null);

    /// <summary>
    ///     Marks the Envelopes in DeadLetterTable
    ///     as replayable. DurabilityAgent will move the envelopes to IncomingTable.
    /// </summary>
    /// <param name="exceptionType">Exception Type that should be marked. Default is any.</param>
    /// <returns>Number of envelopes marked.</returns>
    [Obsolete("Prefer ReplayAsync")]
    Task<int> MarkDeadLetterEnvelopesAsReplayableAsync(string exceptionType = "");

    /// <summary>
    ///     Marks the Envelope in DeadLetterTable
    ///     as replayable. DurabilityAgent will move the envelopes to IncomingTable.
    /// </summary>
    /// <param name="ids"></param>
    /// <param name="tenantId">Leaving tenantId null will query all tenants</param>
    [Obsolete("Prefer ReplayAsync")]
    Task MarkDeadLetterEnvelopesAsReplayableAsync(Guid[] ids, string? tenantId = null);

    /// <summary>
    /// Deletes the DeadLetterEnvelope from the DeadLetterTable
    /// </summary>
    /// <param name="ids"></param>
    /// <param name="tenantId">Leaving tenantId null will query all tenants</param>
    [Obsolete("Prefer DiscardAsync")]
    Task DeleteDeadLetterEnvelopesAsync(Guid[] ids, string? tenantId = null);
    
    /// <summary>
    /// Fetch a summary of the persisted dead letter queue envelopes
    /// </summary>
    /// <param name="serviceName"></param>
    /// <param name="range"></param>
    /// <param name="token"></param>
    /// <returns></returns>
    Task<IReadOnlyList<DeadLetterQueueCount>> SummarizeAllAsync(string serviceName, TimeRange range,
        CancellationToken token);

    /// <summary>
    /// Query for detailed results of dead letter queued envelopes matching the specified query
    /// </summary>
    /// <param name="query"></param>
    /// <param name="token"></param>
    /// <returns></returns>
    Task<DeadLetterEnvelopeResults> QueryAsync(DeadLetterEnvelopeQuery query, CancellationToken token);

    /// <summary>
    /// Deletes all dead letter envelopes matching the query
    /// </summary>
    /// <param name="query"></param>
    /// <param name="token"></param>
    /// <returns></returns>
    Task DiscardAsync(DeadLetterEnvelopeQuery query, CancellationToken token);
    
    /// <summary>
    /// Marks all dead letter envelopes matching the query as replayable
    /// </summary>
    /// <param name="query"></param>
    /// <param name="token"></param>
    /// <returns></returns>
    Task ReplayAsync(DeadLetterEnvelopeQuery query, CancellationToken token);
}