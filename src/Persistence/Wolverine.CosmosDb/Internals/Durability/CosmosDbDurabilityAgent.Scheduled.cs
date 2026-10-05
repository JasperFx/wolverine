using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;

namespace Wolverine.CosmosDb.Internals.Durability;

public partial class CosmosDbDurabilityAgent
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
            var queryText =
                "SELECT * FROM c WHERE c.docType = @docType AND c.status = @status AND c.executionTime <= @now ORDER BY c.executionTime OFFSET 0 LIMIT @limit";
            var query = new QueryDefinition(queryText)
                .WithParameter("@docType", DocumentTypes.Incoming)
                .WithParameter("@status", EnvelopeStatus.Scheduled)
                .WithParameter("@now", DateTimeOffset.UtcNow)
                .WithParameter("@limit", _settings.RecoveryBatchSize);

            var incoming = new List<IncomingMessage>();
            using var iterator = _container.GetItemQueryIterator<IncomingMessage>(query);

            while (iterator.HasMoreResults)
            {
                var response = await iterator.ReadNextAsync(_combined.Token);
                incoming.AddRange(response);
            }

            if (!incoming.Any())
            {
                return;
            }

            await locallyPublishScheduledMessages(incoming);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error while trying to process scheduled messages");
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

    /// <summary>
    /// GH-4711, the twin of GH-4710. Deliberately enqueues every promoted envelope locally rather than routing
    /// it by <c>Destination</c> through <c>runtime.EnqueueDirectlyAsync</c> the way every relational store's
    /// poller does. The difference is real but costs nothing for the shapes that reach this method -- verified
    /// by running rather than by reading, in <c>CosmosDbTests.scheduled_promotion_semantics</c>.
    ///
    /// <para>A delayed message bound for an external endpoint does not park here under that destination. It
    /// parks as a <c>ScheduledEnvelope</c> WRAPPER addressed to this node's own <c>local://durable/</c>, so
    /// enqueuing locally is exactly what has to happen: <c>ScheduledSendEnvelopeHandler</c> then unwraps it and
    /// sends it on to the real destination. A DURABLE external destination takes a different path again -- it
    /// schedules in the outbox and never reaches this poller at all.</para>
    ///
    /// <para>So the three behaviours <c>EnqueueDirectlyAsync</c> carries need no CosmosDb equivalent here:
    /// GH-4645's forward-and-retire (nothing arrives addressed elsewhere), GH-4700's slot-ownership hand-off
    /// (a sharded-queue global partition is not plausibly paired with CosmosDb persistence), and GH-3413's
    /// dead-lettering of an unresolvable destination -- the one case left unverified, and reachable only by
    /// hand-writing a document with a transport this node cannot resolve.</para>
    ///
    /// <para>GH-4711 also asked that this loop not get longer: the promotion above still issues one
    /// <c>ReplaceItemAsync</c> per message and this change adds nothing to it.</para>
    /// </summary>
    private async Task locallyPublishScheduledMessages(List<IncomingMessage> incoming)
    {
        var envelopes = incoming.Select(x => x.Read()).ToList();

        // GH-4216. Stamp the envelope's owning store on each promoted message so the rest of the
        // pipeline (DelegatingMessageInbox, DurableReceiver._markAsHandled) routes its writes back
        // to THIS store. Every relational store does this in its own poller; without it an
        // ancillary CosmosDb store's rows are marked handled against the MAIN store, match nothing,
        // and are re-promoted on every pass. See GH-2576.
        foreach (var envelope in envelopes)
        {
            envelope.Store = _parent;
        }

        foreach (var message in incoming)
        {
            message.Status = EnvelopeStatus.Incoming;
            message.OwnerId = _settings.AssignedNodeNumber;
            await _container.ReplaceItemAsync(message, message.Id,
                new PartitionKey(message.PartitionKey));
        }

        foreach (var envelope in envelopes)
        {
            _logger.LogInformation("Locally enqueuing scheduled message {Id} of type {MessageType}", envelope.Id,
                envelope.MessageType);
            await _localQueue.EnqueueAsync(envelope);
        }
    }
}
