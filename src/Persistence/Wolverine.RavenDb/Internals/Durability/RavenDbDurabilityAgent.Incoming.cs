using Microsoft.Extensions.Logging;
using Raven.Client.Documents;
using Wolverine.Configuration;
using Wolverine.Logging;
using Wolverine.Persistence.Durability;
using Wolverine.Transports;

namespace Wolverine.RavenDb.Internals.Durability;

public partial class RavenDbDurabilityAgent
{

    private async Task tryRecoverIncomingMessages()
    {
        try
        {
            var listeners = await findListenersWithRecoverableIncomingAsync();

            foreach (var receivedAt in listeners)
            {
                // GH-3590: exclusive and leader-pinned listeners run on exactly one node, which is not
                // necessarily this one. GH-4776: so does a global partition's companion local queue, even
                // though its local:// address is live everywhere. Those endpoints recover their own inbox
                // (ListenerInboxRecovery). Checked before the circuit lookup because FindListenerCircuit()
                // falls back to the durable local queue and would otherwise mis-route another node's
                // messages here.
                if (_runtime.Endpoints.ListenerOwnsItsInboxRecovery(receivedAt))
                {
                    continue;
                }

                // circuit can be null when the URI isn't serviced by this node
                var circuit = _runtime.Endpoints.FindListenerCircuit(receivedAt);
                if (circuit == null)
                {
                    // GH-4807: or because nothing is registered under that address at all, in which case
                    // the documents are stranded and the silence was the whole reason it took a bug report
                    // to find out.
                    _logger.LogWarning(
                        "Found recoverable incoming messages in the inbox for destination {Destination}, but no listening endpoint could be resolved for that address. These messages cannot be recovered and will stay in the inbox until an endpoint listening at that address exists",
                        receivedAt);
                    continue;
                }

                if (circuit.Status != ListeningStatus.Accepting)
                {
                    continue;
                }

                // GH-5587. Each listener recovers on its own lane, so one that is slow to take its messages holds up
                // neither the other listeners nor the outgoing recovery that follows. A listener still taking an
                // earlier page is skipped on this pass, before anything more is claimed for it.
                var listener = receivedAt;
                _lanes.TryStart(RecoveryDirection.Incoming, listener,
                    () => recoverMessagesForListener(listener, circuit));
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error trying to recover messages from the durable inbox");
        }
    }

    /// <summary>
    /// GH-4785. The listeners that actually have recoverable inbox documents. Both halves of the filter
    /// matter: owner 0 on its own also matches every <c>Handled</c> document retained for idempotency --
    /// and <see cref="RavenDbMessageStore.LoadPageOfGloballyOwnedIncomingAsync"/> filters those back out --
    /// so each one would buy a spurious <c>WaitForNonStaleResults</c> page query every polling cycle for
    /// the whole retention window. Mirrors the relational <c>CheckRecoverableIncomingMessagesOperation</c>,
    /// which has always been <c>status = 'Incoming' and owner_id = 0</c>.
    /// </summary>
    private async Task<IReadOnlyList<Uri>> findListenersWithRecoverableIncomingAsync()
    {
        using var session = _store.OpenAsyncSession();
        var listeners = await session.Query<IncomingMessage>()
            .Where(x => x.OwnerId == TransportConstants.AnyNode && x.Status == EnvelopeStatus.Incoming)
            .Select(x => new { x.ReceivedAt })
            .Distinct()
            .ToListAsync();

        return listeners.Where(x => x.ReceivedAt != null).Select(x => x.ReceivedAt!).ToList();
    }

    private async Task recoverMessagesForListener(Uri listener, IListenerCircuit circuit)
    {
        try
        {
            // GH-5587. Up to RecoveryBatchSize per pass, and only what the listener has room for, like relational
            // recovery: an exact fit is allowed, with one slot of headroom only when the batch would exceed the limit.
            // This snapshot is not a reservation.
            await DestinationRecovery.RecoverIncomingAsync(_parent, listener, circuit, _settings.RecoveryBatchSize,
                remaining => RecoverIncomingMessagesCommand.PageSizeFor(circuit, remaining, _settings), _settings,
                _logger, _lanes, _combined.Token);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error trying to recover messages from the inbox for listener {Uri}", listener);
        }
    }

    /// <summary>
    /// GH-5587. Wait for the recovery this agent has started to finish. For tests
    /// </summary>
    internal Task WaitForRecoveryLanesAsync() => _lanes.WaitForIdleAsync();
}
