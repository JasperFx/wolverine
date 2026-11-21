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

            await promoteScheduledMessages(incoming);
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
    /// GH-5713. Hands every promoted envelope to the listener for its own <c>Destination</c>, through
    /// <c>runtime.EnqueueDirectlyAsync</c>, like every relational store's poller. It used to enqueue everything onto
    /// this node's <c>local://scheduled</c> queue (GH-4711). That ran a message scheduled for a durable local queue on a
    /// queue that did not own its inbox document, so at shutdown the owning queue handed the document back to any
    /// node while the message was still running, and the durability agent recovered it again on every pass until
    /// the work finished.
    ///
    /// <para>A delayed message for an external endpoint still parks as a <c>ScheduledEnvelope</c> wrapper addressed to
    /// this node's <c>local://durable/</c>, and <c>ScheduledSendEnvelopeHandler</c> unwraps it and sends it on, so
    /// routing by <c>Destination</c> delivers it there as before. <c>EnqueueDirectlyAsync</c> also hands back to any
    /// node whatever it could not hand over.</para>
    /// </summary>
    private async Task promoteScheduledMessages(List<IncomingMessage> incoming)
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
            _logger.LogInformation("Promoting scheduled message {Id} of type {MessageType} to {Destination}",
                envelope.Id, envelope.MessageType, envelope.Destination);
        }

        await _runtime.EnqueueDirectlyAsync(envelopes);
    }
}
