using JasperFx.Core;
using JasperFx.Core.Reflection;
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

    /// <summary>
    /// Updates the body of a dead letter envelope and marks it as replayable
    /// </summary>
    /// <param name="envelopeId">The envelope to edit</param>
    /// <param name="newBody">The new serialized message body</param>
    /// <param name="token"></param>
    /// <returns></returns>
    Task EditAndReplayAsync(Guid envelopeId, byte[] newBody, CancellationToken token);

    /// <summary>
    /// The optional dead letter features this store supports
    /// </summary>
    DeadLetterCapabilities Capabilities => DeadLetterCapabilities.None;

    /// <summary>
    /// Fetch a summary of the persisted dead letter envelopes, grouped by failure group, message type,
    /// endpoint or exception type, with the first and last failure time of each group
    /// </summary>
    /// <param name="serviceName"></param>
    /// <param name="options"></param>
    /// <param name="token"></param>
    /// <returns></returns>
    Task<IReadOnlyList<DeadLetterGroupSummary>> SummarizeAllAsync(string serviceName, DeadLetterSummaryOptions options,
        CancellationToken token)
    {
        return DeadLetterSummaries.SummarizeByQueryingAsync(this, serviceName, options, token);
    }

    /// <summary>
    /// Marks all dead letter envelopes matching the query as replayable, and reports how many were marked.
    /// Archived dead letters are never replayed
    /// </summary>
    /// <param name="query"></param>
    /// <param name="token"></param>
    /// <returns></returns>
    async Task<DeadLetterOperationResult> ReplayMatchingAsync(DeadLetterEnvelopeQuery query, CancellationToken token)
    {
        await ReplayAsync(query, token);
        return DeadLetterOperationResult.NotReported;
    }

    /// <summary>
    /// Deletes all dead letter envelopes matching the query, and reports how many were deleted
    /// </summary>
    /// <param name="query"></param>
    /// <param name="token"></param>
    /// <returns></returns>
    async Task<DeadLetterOperationResult> DiscardMatchingAsync(DeadLetterEnvelopeQuery query, CancellationToken token)
    {
        await DiscardAsync(query, token);
        return DeadLetterOperationResult.NotReported;
    }

    /// <summary>
    /// Archives all dead letter envelopes matching the query that are not already archived, and reports how
    /// many were archived. Archived dead letters are left out of summaries, queries and replays unless asked
    /// for. See <see cref="Capabilities"/>
    /// </summary>
    /// <param name="query"></param>
    /// <param name="token"></param>
    /// <returns></returns>
    Task<DeadLetterOperationResult> ArchiveMatchingAsync(DeadLetterEnvelopeQuery query, CancellationToken token)
    {
        throw new NotSupportedException($"{GetType().FullNameInCode()} does not support archiving dead letters");
    }

    /// <summary>
    /// Restores all archived dead letter envelopes matching the query, and reports how many were restored
    /// </summary>
    /// <param name="query"></param>
    /// <param name="token"></param>
    /// <returns></returns>
    Task<DeadLetterOperationResult> RestoreMatchingAsync(DeadLetterEnvelopeQuery query, CancellationToken token)
    {
        throw new NotSupportedException($"{GetType().FullNameInCode()} does not support archiving dead letters");
    }
}