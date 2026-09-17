using Microsoft.Extensions.Logging;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace Wolverine.RavenDb.Internals.Durability;

public partial class RavenDbDurabilityAgent
{
    private async Task runScheduledJobs()
    {
        try
        {
            if (!(await _parent.TryAttainScheduledJobLockAsync(_combined.Token)))
            {
                return;
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error trying to attain the scheduled job lock");
            return;
        }
        
        try
        {
            using var session = _store.OpenAsyncSession();
            var incoming = await session.Query<IncomingMessage>()
                .Where(x => x.Status == EnvelopeStatus.Scheduled && x.ExecutionTime <= DateTimeOffset.UtcNow)
                .OrderBy(x => x.ExecutionTime)
                .Take(_settings.RecoveryBatchSize)
                .ToListAsync(_combined.Token);

            if (!incoming.Any())
            {
                return;
            }
            
            await locallyPublishScheduledMessages(incoming, session);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error while trying to process ");
        }
        finally
        {
            try
            {
                await _parent.ReleaseScheduledJobLockAsync();
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error trying to release the scheduled job lock");
            }
        }
    }

    private async Task locallyPublishScheduledMessages(List<IncomingMessage> incoming, IAsyncDocumentSession session)
    {
        var envelopes = incoming.Select(x => x.Read()).ToList();

        // GH-4216. Stamp the envelope's owning store on each promoted message so the rest of the
        // pipeline (DelegatingMessageInbox, DurableReceiver._markAsHandled) routes its writes back
        // to THIS store. Every relational store does this in its own poller; without it an
        // ancillary RavenDb store's rows are marked handled against the MAIN store, match nothing,
        // and are re-promoted on every pass. See GH-2576.
        foreach (var envelope in envelopes)
        {
            envelope.Store = _parent;
        }

        foreach (var message in incoming)
        {
            message.Status = EnvelopeStatus.Incoming;
            message.OwnerId = _settings.AssignedNodeNumber;
        }
            
        await session.SaveChangesAsync();

        // This is very low risk
        foreach (var envelope in envelopes)
        {
            _logger.LogInformation("Locally enqueuing scheduled message {Id} of type {MessageType}", envelope.Id,
                envelope.MessageType);
            await _localQueue.EnqueueAsync(envelope);
        }
    }
}