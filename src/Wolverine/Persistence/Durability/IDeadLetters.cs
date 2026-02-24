using JasperFx.Core;
using Wolverine.Persistence.Durability.DeadLetterManagement;

namespace Wolverine.Persistence.Durability;

public static class DeadLettersExtensions
{
    /// <summary>
    ///     Marks the Envelopes in DeadLetterTable
    ///     as replayable. DurabilityAgent will move the envelopes to IncomingTable.
    /// </summary>
    /// <param name="exceptionType">Exception Type that should be marked. Default is any.</param>
    /// <returns>Number of envelopes marked.</returns>
    public static Task MarkDeadLetterEnvelopesAsReplayableAsync(this IDeadLetters letters, string exceptionType = "")
    {
        return letters.ReplayAsync(new DeadLetterEnvelopeQuery
            { Range = TimeRange.AllTime(), ExceptionType = exceptionType }, CancellationToken.None);
    }
    
    /// <summary>
    /// Polyfill for old API
    /// </summary>
    /// <param name="letters"></param>
    /// <param name="ids"></param>
    /// <returns></returns>
    public static Task MarkDeadLetterEnvelopesAsReplayableAsync(this IDeadLetters letters, Guid[] ids)
    {
        return letters.ReplayAsync(new DeadLetterEnvelopeQuery
            { MessageIds = ids}, CancellationToken.None);
    }
}

/// <summary>
/// This is the original V2/3 service for dead letter querying
/// </summary>
public interface IDeadLetters
{
    /// <param name="tenantId">Leaving tenantId null will query all tenants</param>
    Task<DeadLetterEnvelope?> DeadLetterEnvelopeByIdAsync(Guid id, string? tenantId = null);

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