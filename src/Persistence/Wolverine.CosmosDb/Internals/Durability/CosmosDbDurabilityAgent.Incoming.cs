using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using Wolverine.Logging;
using Wolverine.Transports;

namespace Wolverine.CosmosDb.Internals.Durability;

public partial class CosmosDbDurabilityAgent
{
    private async Task tryRecoverIncomingMessages()
    {
        try
        {
            var queryText =
                "SELECT DISTINCT c.receivedAt FROM c WHERE c.docType = @docType AND c.ownerId = 0";
            var query = new QueryDefinition(queryText)
                .WithParameter("@docType", DocumentTypes.Incoming);

            using var iterator = _container.GetItemQueryIterator<dynamic>(query);

            var listeners = new List<string>();
            while (iterator.HasMoreResults)
            {
                var response = await iterator.ReadNextAsync();
                foreach (var item in response)
                {
                    string? receivedAt = item.receivedAt;
                    if (receivedAt != null)
                    {
                        listeners.Add(receivedAt);
                    }
                }
            }

            foreach (var listenerStr in listeners)
            {
                var receivedAt = new Uri(listenerStr);
                var circuit = _runtime.Endpoints.FindListenerCircuit(receivedAt);
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
