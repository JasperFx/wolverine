using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;

namespace Wolverine.CosmosDb.Internals.Durability;

public partial class CosmosDbDurabilityAgent
{
    private async Task tryRecoverOutgoingMessagesAsync()
    {
        try
        {
            var queryText =
                "SELECT DISTINCT c.destination FROM c WHERE c.docType = @docType AND c.ownerId = 0";
            var query = new QueryDefinition(queryText)
                .WithParameter("@docType", DocumentTypes.Outgoing);

            using var iterator = _container.GetItemQueryIterator<dynamic>(query);

            var destinations = new List<string>();
            while (iterator.HasMoreResults)
            {
                var response = await iterator.ReadNextAsync();
                foreach (var item in response)
                {
                    string? destination = item.destination;
                    if (destination != null)
                    {
                        destinations.Add(destination);
                    }
                }
            }

            foreach (var destinationStr in destinations)
            {
                await tryRecoverOutgoingMessagesToSenderAsync(new Uri(destinationStr));
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error trying to recover orphaned outgoing messages");
        }
    }

    private async Task tryRecoverOutgoingMessagesToSenderAsync(Uri sender)
    {
        try
        {
            var sendingAgent = _runtime.Endpoints.GetOrBuildSendingAgent(sender);
            if (sendingAgent.Latched) return;

            var outgoing = await _parent.Outbox.LoadOutgoingAsync(sendingAgent.Destination);
            var expiredMessages = outgoing.Where(x => x.IsExpired()).ToArray();
            var good = outgoing.Where(x => !x.IsExpired()).ToArray();

            await _parent.Outbox.DiscardAndReassignOutgoingAsync(expiredMessages, good,
                _runtime.Options.Durability.AssignedNodeNumber);

            foreach (var envelope in good) await sendingAgent.EnqueueOutgoingAsync(envelope);

            _logger.LogInformation(
                "Recovered {Count} messages from outbox for destination {Destination} while discarding {ExpiredCount} expired messages",
                good.Length, sendingAgent.Destination, expiredMessages.Length);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error trying to find a sending agent for {Destination}", sender);
        }
    }
}
