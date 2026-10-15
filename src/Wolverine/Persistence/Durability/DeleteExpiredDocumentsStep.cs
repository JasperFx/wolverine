using Microsoft.Extensions.Logging;

namespace Wolverine.Persistence.Durability;

/// <summary>
///     GH-5036. What a document message store (RavenDb, Cosmos DB) answers so
///     <see cref="DeleteExpiredDocumentsStep" /> can do the expiry cleanups of a durability pass
/// </summary>
public interface IExpiredDocumentSource
{
    /// <summary>
    ///     Delete up to <paramref name="limit" /> <c>Handled</c> inbox documents whose keep-until time is at or
    ///     before <paramref name="now" />, and answer how many were deleted
    /// </summary>
    Task<int> DeleteExpiredHandledAsync(DateTimeOffset now, int limit, CancellationToken token);

    /// <summary>
    ///     How many documents <see cref="DeleteExpiredHandledAsync" /> would still find, ignoring the limit
    /// </summary>
    Task<int> CountExpiredHandledAsync(DateTimeOffset now, CancellationToken token);

    /// <summary>
    ///     Delete every dead letter whose expiration time is before <paramref name="now" />, and answer how many
    ///     were deleted
    /// </summary>
    Task<int> DeleteExpiredDeadLettersAsync(DateTimeOffset now, CancellationToken token);
}

/// <summary>
///     GH-5036. The expiry cleanups of a <see cref="DurabilityMode.ServerlessWithOutbox" /> durability pass for a
///     document store, which has no durability agent running to do them: one batch of expired handled inbox
///     documents per call, then the expired dead letters (when
///     <see cref="DurabilitySettings.DeadLetterQueueExpirationEnabled" />) once per pass
/// </summary>
public sealed class DeleteExpiredDocumentsStep : IDurabilityPassStep
{
    private readonly IMessageStore _store;
    private readonly IExpiredDocumentSource _source;
    private readonly ILogger _logger;
    private bool _deadLettersDone;

    public DeleteExpiredDocumentsStep(IMessageStore store, IExpiredDocumentSource source, ILogger logger)
    {
        _store = store;
        _source = source;
        _logger = logger;
    }

    public string Name => "Delete expired documents";

    public async Task<DurabilityPassStepResult> ExecuteAsync(DurabilityPassContext context, CancellationToken token)
    {
        if (_store.HasDisposed) return DurabilityPassStepResult.Nothing;

        var now = context.Now;

        var handled = await _source.DeleteExpiredHandledAsync(now, context.BatchSize, token);
        var deleted = handled;

        // Not batched, so it only needs to run once per pass
        if (!_deadLettersDone)
        {
            _deadLettersDone = true;

            if (context.Runtime.Options.Durability.DeadLetterQueueExpirationEnabled)
            {
                deleted += await _source.DeleteExpiredDeadLettersAsync(now, token);
            }
        }

        var remaining = handled < context.BatchSize ? 0 : await _source.CountExpiredHandledAsync(now, token);

        if (deleted > 0)
        {
            _logger.LogInformation("Durability pass deleted {Count} expired document(s) from {Store}", deleted,
                _store.Uri);
        }

        return new DurabilityPassStepResult { ExpiredDeleted = deleted, Remaining = remaining };
    }
}
