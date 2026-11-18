using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using Wolverine.Persistence.Durability;

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
                // GH-5587. Each destination sends on its own lane, so one that is slow to accept sends holds up no
                // other destination, and the next pass's incoming recovery does not wait on it either
                var destination = new Uri(destinationStr);
                _lanes.TryStart(RecoveryDirection.Outgoing, destination,
                    () => tryRecoverOutgoingMessagesToSenderAsync(destination));
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
            await DestinationRecovery.RecoverOutgoingAsync(_parent, sendingAgent,
                _runtime.Options.Durability.AssignedNodeNumber, _logger, _lanes, _combined.Token);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error trying to find a sending agent for {Destination}", sender);
        }
    }
}
