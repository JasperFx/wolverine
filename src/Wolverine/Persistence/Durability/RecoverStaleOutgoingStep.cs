using Microsoft.Extensions.Logging;
using Wolverine.Runtime;
using Wolverine.Transports.Sending;

namespace Wolverine.Persistence.Durability;

/// <summary>
///     GH-5016. What a message store answers so <see cref="RecoverStaleOutgoingStep" /> can recover
///     the outbox rows whose inline send failed under <see cref="DurabilityMode.ServerlessWithOutbox" />
/// </summary>
public interface IStaleOutgoingSource
{
    /// <summary>
    ///     Load up to <paramref name="limit" /> outgoing envelopes owned by any node (<c>owner_id = 0</c>),
    ///     oldest first. When <paramref name="staleBefore" /> has a value, only rows written at or before it
    ///     qualify, so a pass never races the request that is still sending a row inline. Rows whose ids are in
    ///     <paramref name="excluded" /> are skipped
    /// </summary>
    Task<IReadOnlyList<Envelope>> LoadStaleOutgoingAsync(DateTimeOffset? staleBefore, Guid[] excluded, int limit,
        CancellationToken token);

    /// <summary>
    ///     How many rows <see cref="LoadStaleOutgoingAsync" /> would still find, ignoring the limit
    /// </summary>
    Task<int> CountStaleOutgoingAsync(DateTimeOffset? staleBefore, Guid[] excluded, CancellationToken token);

    /// <summary>
    ///     GH-5050. Restart the stale clock (the written time) of these owner-0 rows, whose send just failed, at the
    ///     store's current time. They go to the back of the oldest-first order and are not stale again for
    ///     <see cref="DurabilitySettings.OutboxStaleTime" />, so rows that keep failing cannot starve the rows behind
    ///     them and are retried with a natural backoff. Only called when <see cref="DurabilitySettings.OutboxStaleTime" />
    ///     has a value
    /// </summary>
    Task RestartStaleOutgoingAsync(IReadOnlyList<Envelope> envelopes, CancellationToken token);
}

/// <summary>
///     GH-5016. The outbox recovery step of a <see cref="DurabilityMode.ServerlessWithOutbox" /> durability
///     pass: one batch of stale owner-0 rows per call, each sent straight through its endpoint's transport
///     sender, then everything sent (or expired) deleted in <b>one</b> batched call
/// </summary>
/// <remarks>
///     A row whose send fails stays in the outbox for a later pass, and is not tried again in this one. Its stale
///     clock is restarted, so it moves to the back of the stale order and waits <see cref="DurabilitySettings.OutboxStaleTime" />
///     before a pass tries it again (GH-5050). A row the
///     transport reports as a serialization failure, which no retry can fix, is moved to dead letter storage
///     (GH-5047)
/// </remarks>
public sealed class RecoverStaleOutgoingStep : IDurabilityPassStep
{
    private readonly IMessageStore _store;
    private readonly IStaleOutgoingSource _source;
    private readonly ILogger _logger;
    private readonly HashSet<Guid> _failed = new();

    // The failed rows whose stale clock could not be restarted, so they still count as stale. See GH-5050
    private readonly HashSet<Guid> _stillStale = new();
    private int _remaining;

    public RecoverStaleOutgoingStep(IMessageStore store, IStaleOutgoingSource source, ILogger logger)
    {
        _store = store;
        _source = source;
        _logger = logger;
    }

    public string Name => "Recover stale outgoing messages";

    public async Task<DurabilityPassStepResult> ExecuteAsync(DurabilityPassContext context, CancellationToken token)
    {
        var runtime = context.Runtime;
        var staleTime = runtime.Options.Durability.OutboxStaleTime;
        DateTimeOffset? staleBefore = staleTime.HasValue ? context.Now.Subtract(staleTime.Value) : null;

        if (_failed.Count > DurabilityPassContext.MaxExcludedIds)
        {
            // Nothing done, so the pass moves on. What is left waits for a later pass. See GH-5035
            _logger.LogWarning(
                "Durability pass stopped recovering outgoing messages from {Store} after {Failed} failed to send",
                _store.Uri, _failed.Count);
            return new DurabilityPassStepResult { Remaining = _remaining };
        }

        var page = await _source.LoadStaleOutgoingAsync(staleBefore, _failed.ToArray(), context.BatchSize, token);
        if (page.Count == 0) return DurabilityPassStepResult.Nothing;

        var sent = new List<Envelope>();
        var expired = new List<Envelope>();
        var deadLettered = new List<Envelope>();
        var toSend = new List<(Envelope Envelope, ISender Sender)>();
        var failedEnvelopes = new List<Envelope>();

        foreach (var envelope in page)
        {
            // An in-memory-only reference that does not survive persistence. See GH-4417
            envelope.Store ??= _store;

            if (envelope.IsExpired())
            {
                expired.Add(envelope);
                continue;
            }

            var sender = senderFor(runtime, envelope);
            if (sender == null)
            {
                failedEnvelopes.Add(envelope);
                continue;
            }

            toSend.Add((envelope, sender));
        }

        // One group per transport sender, each sent in as few broker round trips as its transport allows, and the
        // groups concurrently with each other. The rows come oldest first, so each group keeps that order
        var groups = toSend.GroupBy(x => x.Sender).ToArray();
        var sends = groups.Select(group => group.Key.SendToBrokerAsync(group.Select(x => x.Envelope).ToArray()))
            .ToArray();

        await waitForSendsAsync(sends, context, token);

        for (var i = 0; i < groups.Length; i++)
        {
            // A send still running past the pass's time is abandoned rather than awaited. See below
            var result = sends[i].IsCompletedSuccessfully ? await sends[i] : null;

            foreach (var (envelope, _) in groups[i])
            {
                if (result != null && result.WasAccepted(envelope))
                {
                    runtime.MessageTracking.Sent(envelope);
                    sent.Add(envelope);
                    continue;
                }

                // GH-5047. Sending it again in every later pass can never succeed
                if (result != null && result.IsSerializationFailure(envelope, out var serializationFailure) &&
                    await UnsendableOutgoingEnvelopes.TryMoveToDeadLetterStorageAsync(runtime, _store, envelope,
                        serializationFailure, _logger))
                {
                    deadLettered.Add(envelope);
                    continue;
                }

                if (result == null)
                {
                    _logger.LogError(
                        "Gave up waiting on the send of recovered outgoing message {Envelope} to {Destination}. It is left in the outbox for a later durability pass",
                        envelope, envelope.Destination);
                }
                else
                {
                    _logger.LogError(result.Exception,
                        "Unable to send recovered outgoing message {Envelope} to {Destination}. It is left in the outbox for a later durability pass",
                        envelope, envelope.Destination);
                }

                failedEnvelopes.Add(envelope);
            }
        }

        var failed = failedEnvelopes.Count;
        foreach (var envelope in failedEnvelopes) _failed.Add(envelope.Id);

        if (failed > 0 && !(staleTime.HasValue && await tryRestartStaleClockAsync(failedEnvelopes, token)))
        {
            foreach (var envelope in failedEnvelopes) _stillStale.Add(envelope.Id);
        }

        var deletes = sent.Concat(expired).Concat(deadLettered).ToArray();
        if (deletes.Length > 0)
        {
            // The messages are already sent (or dead lettered), so a failure here means a duplicate send (or a
            // second dead letter) by a later pass, which at-least-once delivery allows
            await _store.Outbox.DeleteOutgoingAsync(deletes);
        }

        foreach (var envelope in expired)
        {
            runtime.MessageTracking.DiscardedEnvelope(envelope);
        }

        // Past the cap the id list is not sent. The failed rows whose stale clock was restarted are not stale any
        // more, so they are not counted as left either way (GH-5050)
        var remaining = page.Count < context.BatchSize
            ? 0
            : await _source.CountStaleOutgoingAsync(staleBefore, excludedIds(), token);
        _remaining = remaining;

        if (sent.Count + expired.Count + deadLettered.Count + failed > 0)
        {
            _logger.LogInformation(
                "Durability pass recovered {Sent} outgoing message(s) from the outbox of {Store}, discarded {Expired} expired, moved {DeadLettered} that can never be sent to dead letter storage and left {Failed} that failed to send",
                sent.Count, _store.Uri, expired.Count, deadLettered.Count, failed);
        }

        return new DurabilityPassStepResult
        {
            OutgoingSent = sent.Count,
            OutgoingFailed = failed,
            OutgoingDeadLettered = deadLettered.Count,
            ExpiredDeleted = expired.Count,
            Remaining = remaining,
            NewlyExcluded = failed
        };
    }

    /// <summary>
    ///     How long past the pass's deadline a send may still run before it is abandoned
    /// </summary>
    internal TimeSpan SendGrace { get; set; } = TimeSpan.FromSeconds(5);

    // A broker send takes no cancellation token, so a hung one would otherwise hold the pass -- and its lock, so
    // every other pass skips this store -- for as long as it hangs. Stop waiting at the pass's deadline plus a
    // grace period, or when the pass is cancelled, and count whatever has not finished as failed. Such a send may
    // still complete later, and then a later pass sends that message again, which at-least-once delivery allows
    private async Task waitForSendsAsync(Task[] sends, DurabilityPassContext context, CancellationToken token)
    {
        if (sends.Length == 0) return;

        var remaining = context.TimeRemaining;
        var bound = (remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero) + SendGrace;

        try
        {
            await Task.WhenAll(sends).WaitAsync(bound, token);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Durability pass stopped waiting on {Count} outgoing send(s) to recover from {Store} after {Bound}",
                sends.Count(x => !x.IsCompleted), _store.Uri, bound);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Settle what did finish below, then the runner sees the cancellation
        }
        catch (Exception e)
        {
            // A grouped send reports failures rather than throwing, so this is unexpected. Its group counts as failed
            _logger.LogError(e, "Unexpected failure while recovering outgoing messages from {Store}", _store.Uri);
        }
    }

    // GH-5050. Without this every pass starts again from the same oldest rows, so more failing rows than a pass
    // can exclude (MaxExcludedIds) starve every healthy row behind them for good, and a drain loop on HasMoreWork
    // never ends. The ids stay excluded for the rest of this pass as well, whatever the clocks say
    private async Task<bool> tryRestartStaleClockAsync(List<Envelope> failed, CancellationToken token)
    {
        try
        {
            foreach (var chunk in failed.Chunk(DurabilityPassContext.MaxExcludedIds))
            {
                await _source.RestartStaleOutgoingAsync(chunk, token);
            }

            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            // The rows are still in the outbox, so nothing is lost. They are only tried again sooner
            _logger.LogError(e,
                "Unable to restart the stale clock of {Count} outgoing message(s) in {Store} that failed to send",
                failed.Count, _store.Uri);
            return false;
        }
    }

    // What the count leaves out: only the failed rows that are still stale. A row whose clock was restarted is not
    // stale any more, and a store that counts by subtracting the excluded ids (RavenDb, Cosmos DB) would otherwise
    // take it off twice
    private Guid[] excludedIds()
    {
        return _stillStale.Count > DurabilityPassContext.MaxExcludedIds ? [] : _stillStale.ToArray();
    }

    private ISender? senderFor(IWolverineRuntime runtime, Envelope envelope)
    {
        ISendingAgent agent;
        try
        {
            agent = runtime.Endpoints.GetOrBuildSendingAgent(envelope.Destination!);
        }
        catch (Exception e)
        {
            // A leftover row for a transport that was removed must not stop the rest. See GH-3413
            _logger.LogError(e, "Error trying to find a sending agent for {Destination}", envelope.Destination);
            return null;
        }

        var sender = agent switch
        {
            SendingAgent a => a.Sender,
            InlineSendingAgent a => a.Sender,
            ISender s => s,
            _ => null
        };

        if (sender == null || agent.Latched)
        {
            _logger.LogWarning(
                "Unable to send recovered outgoing message {Envelope} to {Destination} inline, so it is left in the outbox",
                envelope, envelope.Destination);
            return null;
        }

        // The sender wire tap is in-memory only as well. See GH-3263
        envelope.WireTap ??= agent.Endpoint.WireTap;
        envelope.Status = EnvelopeStatus.Outgoing;

        return sender;
    }
}
