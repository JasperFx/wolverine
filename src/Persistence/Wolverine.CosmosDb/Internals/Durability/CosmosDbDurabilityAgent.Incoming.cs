using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Logging;
using Wolverine.Transports;

namespace Wolverine.CosmosDb.Internals.Durability;

public partial class CosmosDbDurabilityAgent
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

                var circuit = _runtime.Endpoints.FindListenerCircuit(receivedAt);
                if (circuit == null)
                {
                    // GH-4807: nothing is registered under that address, so the documents are stranded.
                    // This used to be silent, which is why it took a bug report to find out.
                    _logger.LogWarning(
                        "Found recoverable incoming messages in the inbox for destination {Destination}, but no listening endpoint could be resolved for that address. These messages cannot be recovered and will stay in the inbox until an endpoint listening at that address exists",
                        receivedAt);
                    continue;
                }

                if (circuit.Status != ListeningStatus.Accepting)
                {
                    continue;
                }

                await recoverMessagesForListener(receivedAt, circuit);
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error trying to recover messages from the durable inbox");
        }
    }

    /// <summary>
    /// GH-4784. The listeners that actually have recoverable inbox documents. Both halves of the filter
    /// matter: owner 0 on its own also matches every <c>Handled</c> document retained for idempotency --
    /// and <see cref="CosmosDbMessageStore.LoadPageOfGloballyOwnedIncomingAsync"/> filters those back out --
    /// so each one would buy a spurious per-partition page query every polling cycle for the whole retention
    /// window. This query is deliberately cross-partition, so the retained documents are not screened out by
    /// a partition key either. Mirrors the relational <c>CheckRecoverableIncomingMessagesOperation</c>, which
    /// has always been <c>status = 'Incoming' and owner_id = 0</c>.
    /// </summary>
    private async Task<IReadOnlyList<Uri>> findListenersWithRecoverableIncomingAsync()
    {
        var queryText =
            "SELECT DISTINCT c.receivedAt FROM c WHERE c.docType = @docType AND c.ownerId = @ownerId AND c.status = @status";
        var query = new QueryDefinition(queryText)
            .WithParameter("@docType", DocumentTypes.Incoming)
            .WithParameter("@ownerId", TransportConstants.AnyNode)
            .WithParameter("@status", EnvelopeStatus.Incoming);

        using var iterator = _container.GetItemQueryIterator<dynamic>(query);

        var listeners = new List<Uri>();
        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            foreach (var item in response)
            {
                string? receivedAt = item.receivedAt;
                if (receivedAt != null)
                {
                    listeners.Add(new Uri(receivedAt));
                }
            }
        }

        return listeners;
    }

    private async Task recoverMessagesForListener(Uri listener, IListenerCircuit circuit)
    {
        try
        {
            var envelopes = await _parent.LoadPageOfGloballyOwnedIncomingAsync(listener,
                _settings.RecoveryBatchSize);
            await _parent.ReassignIncomingAsync(_settings.AssignedNodeNumber, envelopes);

            await circuit.EnqueueDirectlyAsync(envelopes);
            _logger.RecoveredIncoming(envelopes);

            _logger.LogInformation(
                "Successfully recovered {Count} messages from the inbox for listener {Listener}",
                envelopes.Count, listener);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error trying to recover messages from the inbox for listener {Uri}", listener);
        }
    }
}
