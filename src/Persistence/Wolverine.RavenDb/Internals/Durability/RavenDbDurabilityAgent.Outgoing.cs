using Microsoft.Extensions.Logging;
using Raven.Client.Documents;

namespace Wolverine.RavenDb.Internals.Durability;

public partial class RavenDbDurabilityAgent
{
    private async Task tryRecoverOutgoingMessagesAsync()
    {
        try
        {
            using var session = _store.OpenAsyncSession();

            var senders = (await session.Query<OutgoingMessage>().Customize(x => x.WaitForNonStaleResults())
                .Where(x => x.OwnerId == 0).ToListAsync())
                .Select(x => x.Destination)
                .Distinct()
                .ToList();

            foreach (var sender in senders)
            {
                await tryRecoverOutgoingMessagesToSenderAsync(sender);
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