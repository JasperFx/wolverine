using Microsoft.Extensions.Logging;
using Raven.Client.Documents;
using Wolverine.Logging;
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
                if (circuit == null || circuit.Status != ListeningStatus.Accepting)
                {
                    continue;
                }

                // Harden around this!
                await recoverMessagesForListener(receivedAt, circuit);
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
            var envelopes = await _parent.LoadPageOfGloballyOwnedIncomingAsync(listener, _settings.RecoveryBatchSize);
            await _parent.ReassignIncomingAsync(_settings.AssignedNodeNumber, envelopes);

            await circuit.EnqueueDirectlyAsync(envelopes);
            _logger.RecoveredIncoming(envelopes);

            if (envelopes.Count > 0)
            {
                _logger.LogInformation("Successfully recovered {Count} messages from the inbox for listener {Listener}",
                    envelopes.Count, listener);
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error trying to recover messages from the inbox for listener {Uri}", listener);
        }
    }

}