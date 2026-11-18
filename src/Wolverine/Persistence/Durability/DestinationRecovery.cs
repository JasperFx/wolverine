using Microsoft.Extensions.Logging;
using Wolverine.Logging;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.Persistence.Durability;

/// <summary>
/// GH-5587. Durable recovery for one destination: what each message store's durability agent runs on that
/// destination's <see cref="RecoveryLanes">lane</see>, so that it never waits on any other destination.
/// </summary>
internal static class DestinationRecovery
{
    /// <summary>
    /// The lanes of a running Wolverine application, or null for a runtime that has none (test doubles), in which case
    /// recovery runs in place as it always did
    /// </summary>
    public static RecoveryLanes? LanesFor(IWolverineRuntime runtime)
    {
        return (runtime as WolverineRuntime)?.RecoveryLanes;
    }

    /// <summary>
    /// Recover up to <paramref name="maxCount" /> incoming envelopes for one destination, a page at a time and only
    /// while its listener has room for the next page. Each page is claimed before it is handed to the listener, one
    /// envelope at a time, and whatever the listener did not take is released for any node to recover.
    /// </summary>
    public static async Task RecoverIncomingAsync(IMessageStore store, Uri destination, IListenerCircuit circuit,
        int maxCount, Func<int, int> pageSizeFor, DurabilitySettings settings, ILogger logger, RecoveryLanes? lanes,
        CancellationToken token)
    {
        var remaining = maxCount;
        while (remaining > 0 && !token.IsCancellationRequested)
        {
            // Checked before anything is claimed: a listener with no room gets nothing this pass
            var pageSize = pageSizeFor(remaining);
            if (pageSize <= 0)
            {
                logger.LogInformation(
                    "Unable to recover inbox messages to destination {Destination}. Listener has status {Status}, queued count {QueuedCount}, and BufferingLimits {BufferedLimits}",
                    destination, circuit.Status, circuit.QueueCount, circuit.Endpoint.BufferingLimits);
                return;
            }

            var claimed = lanes == null
                ? await claimIncomingAsync(store, destination, pageSize, settings).ConfigureAwait(false)
                : await lanes.ClaimAsync(() => claimIncomingAsync(store, destination, pageSize, settings), token)
                    .ConfigureAwait(false);

            if (claimed.Count == 0)
            {
                return;
            }

            if (!await admitAsync(store, destination, circuit, claimed, logger, token).ConfigureAwait(false))
            {
                return;
            }

            logger.RecoveredIncoming(claimed);
            logger.LogInformation("Successfully recovered {Count} messages from the inbox for listener {Listener}",
                claimed.Count, destination);

            remaining -= pageSize;
        }
    }

    private static async Task<IReadOnlyList<Envelope>> claimIncomingAsync(IMessageStore store, Uri destination,
        int pageSize, DurabilitySettings settings)
    {
        var candidates = await store.LoadPageOfGloballyOwnedIncomingAsync(destination, pageSize).ConfigureAwait(false);

        // Ensure each recovered envelope carries a reference to the store it was loaded from.
        // This is critical for ancillary stores: without this, the envelope's Store property
        // is null and DelegatingMessageInbox falls back to the main store when marking the
        // envelope as handled — leaving it stuck as "Incoming" in the ancillary store.
        // See https://github.com/JasperFx/wolverine/issues/2318
        foreach (var envelope in candidates)
        {
            envelope.Store ??= store;
        }

        return await store.TryClaimIncomingAsync(settings.AssignedNodeNumber, candidates).ConfigureAwait(false);
    }

    // One envelope at a time, so that a failure or a stop part way through releases only what the listener never took.
    // Releasing an envelope it had already taken would run it twice
    private static async Task<bool> admitAsync(IMessageStore store, Uri destination, IListenerCircuit circuit,
        IReadOnlyList<Envelope> claimed, ILogger logger, CancellationToken token)
    {
        for (var i = 0; i < claimed.Count; i++)
        {
            if (token.IsCancellationRequested)
            {
                await releaseIncomingAsync(store, destination, rest(claimed, i), logger).ConfigureAwait(false);
                return false;
            }

            try
            {
                await circuit.EnqueueDirectlyAsync([claimed[i]]).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                // GH-3680. The rows above are now owned by *this* node, and orphan recovery only ever releases
                // rows owned by a node it has proven to be dead. If the hand-off into the listener fails -- and it
                // absolutely can, because a circuit breaker trip between the page size check and here nulls out the
                // agent's receiver -- then nothing on any node will ever release them again and the messages are
                // lost for the life of the process. Hand ownership back so the next sweep can retry.
                logger.LogError(e,
                    "Error trying to enqueue {Count} recovered messages into the listener for {Destination}. Releasing them back to any node so that they are recovered on a later sweep",
                    claimed.Count - i, destination);

                await releaseIncomingAsync(store, destination, rest(claimed, i), logger).ConfigureAwait(false);
                return false;
            }
        }

        return true;
    }

    private static async Task releaseIncomingAsync(IMessageStore store, Uri destination,
        IReadOnlyList<Envelope> envelopes, ILogger logger)
    {
        try
        {
            await store.ReassignIncomingAsync(TransportConstants.AnyNode, envelopes).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            logger.LogError(e,
                "Error trying to release {Count} un-enqueued messages for {Destination} back to any node",
                envelopes.Count, destination);
        }
    }

    /// <summary>
    /// Claim this node's share of the outbox for one destination and hand each envelope to its sending agent. A stop
    /// part way through, or a sending agent that throws, releases the envelopes it never got for any node to recover.
    /// </summary>
    public static async Task RecoverOutgoingAsync(IMessageStore store, ISendingAgent sendingAgent, int nodeNumber,
        ILogger logger, RecoveryLanes? lanes, CancellationToken token)
    {
        // It's possible that this could happen between the work being issued and run
        if (sendingAgent.Latched)
        {
            return;
        }

        var (expired, good) = lanes == null
            ? await claimOutgoingAsync(store, sendingAgent, nodeNumber).ConfigureAwait(false)
            : await lanes.ClaimAsync(() => claimOutgoingAsync(store, sendingAgent, nodeNumber), token)
                .ConfigureAwait(false);

        for (var i = 0; i < good.Length; i++)
        {
            if (token.IsCancellationRequested)
            {
                await releaseOutgoingAsync(store, sendingAgent, good[i..], logger).ConfigureAwait(false);
                return;
            }

            var envelope = good[i];

            // The sender wire tap is an in-memory-only reference that does not survive
            // persistence, so a recovered outgoing envelope has none. Re-attach it from
            // the sending agent's endpoint so RecordSuccessAsync still fires when the
            // recovered message is sent. See GH-3263.
            envelope.WireTap ??= sendingAgent.Endpoint.WireTap;

            // The originating store is an in-memory-only reference too, for exactly the same
            // reason, and losing it is worse than losing the wire tap: DelegatingMessageOutbox
            // routes every acknowledgement on envelope.Store and falls back to the MAIN store
            // when it is null. An envelope recovered from an ancillary store would therefore be
            // deleted from the main store's outgoing table -- a no-op -- leaving the ancillary
            // row in place to be recovered, sent and handled again on every single restart.
            // Harmless for the main store, where _store.Outbox is already the fallback.
            // This is the outgoing twin of the stamp incoming recovery has applied
            // since GH-2318. See GH-4417.
            envelope.Store ??= store;

            try
            {
                await sendingAgent.EnqueueOutgoingAsync(envelope).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                // The sending agent may have this one, but it never saw the rest
                logger.LogError(e,
                    "Error trying to send recovered outgoing message {EnvelopeId} to {Destination}. Releasing the {Count} messages after it back to any node",
                    envelope.Id, sendingAgent.Destination, good.Length - i - 1);
                await releaseOutgoingAsync(store, sendingAgent, good[(i + 1)..], logger).ConfigureAwait(false);
                return;
            }
        }

        logger.LogInformation(
            "Recovered {Count} messages from outbox for destination {Destination} while discarding {ExpiredCount} expired messages",
            good.Length, sendingAgent.Destination, expired.Length);
    }

    private static async Task<(Envelope[] Expired, Envelope[] Good)> claimOutgoingAsync(IMessageStore store,
        ISendingAgent sendingAgent, int nodeNumber)
    {
        var outgoing = await store.Outbox.LoadOutgoingAsync(sendingAgent.Destination).ConfigureAwait(false);
        var expiredList = new List<Envelope>();
        var goodList = new List<Envelope>();
        foreach (var envelope in outgoing)
        {
            if (envelope.IsExpired())
            {
                expiredList.Add(envelope);
            }
            else
            {
                goodList.Add(envelope);
            }
        }

        var expired = expiredList.ToArray();
        var good = goodList.ToArray();

        await store.Outbox.DiscardAndReassignOutgoingAsync(expired, good, nodeNumber).ConfigureAwait(false);

        return (expired, good);
    }

    private static async Task releaseOutgoingAsync(IMessageStore store, ISendingAgent sendingAgent,
        Envelope[] envelopes, ILogger logger)
    {
        if (envelopes.Length == 0)
        {
            return;
        }

        try
        {
            await store.Outbox.DiscardAndReassignOutgoingAsync([], envelopes, TransportConstants.AnyNode)
                .ConfigureAwait(false);
        }
        catch (Exception e)
        {
            logger.LogError(e,
                "Error trying to release {Count} unsent outgoing messages for {Destination} back to any node",
                envelopes.Length, sendingAgent.Destination);
        }
    }

    private static IReadOnlyList<Envelope> rest(IReadOnlyList<Envelope> envelopes, int start)
    {
        if (start == 0)
        {
            return envelopes;
        }

        var list = new List<Envelope>(envelopes.Count - start);
        for (var i = start; i < envelopes.Count; i++)
        {
            list.Add(envelopes[i]);
        }

        return list;
    }
}
