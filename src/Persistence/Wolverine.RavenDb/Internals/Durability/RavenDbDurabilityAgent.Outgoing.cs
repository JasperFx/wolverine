using Microsoft.Extensions.Logging;
using Raven.Client.Documents;
using Wolverine.Persistence.Durability;

namespace Wolverine.RavenDb.Internals.Durability;

public partial class RavenDbDurabilityAgent
{
    private async Task tryRecoverOutgoingMessagesAsync()
    {
        try
        {
            using var session = _store.OpenAsyncSession();

            var senders = (await session.Query<OutgoingMessage>().Customize(x => x.WaitForNonStaleResults())
                .Where(x => x.OwnerId == 0).ToListAsync(_combined.Token))
                .Select(x => new { x.Destination })
                .Distinct()
                .ToList();

            foreach (var sender in senders.Where(x => x.Destination != null))
            {
                // GH-5587. Each destination sends on its own lane, so one that is slow to accept sends holds up no
                // other destination, and the next pass's incoming recovery does not wait on it either
                var destination = sender.Destination!;
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