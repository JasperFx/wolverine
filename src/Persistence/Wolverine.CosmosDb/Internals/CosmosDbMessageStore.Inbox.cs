using System.Net;
using Microsoft.Azure.Cosmos;
using Wolverine.Persistence.Durability;
using Wolverine.Transports;

namespace Wolverine.CosmosDb.Internals;

public partial class CosmosDbMessageStore : IMessageInbox
{
    public async Task ScheduleExecutionAsync(Envelope envelope)
    {
        if (await readIncomingAsync(envelope) is not var (message, partitionKey))
        {
            // Already gone
            return;
        }

        // GH-4216: a Handled document is never resurrected by a late retry booking. It is the dedup
        // window's record that the message completed
        if (message.Status == EnvelopeStatus.Handled)
        {
            return;
        }

        message.ExecutionTime = envelope.ScheduledTime;
        message.Status = EnvelopeStatus.Scheduled;
        message.Attempts = envelope.Attempts;
        message.OwnerId = 0;
        await replaceIncomingAsync(message, partitionKey);
    }

    public Task DeleteIncomingEnvelopeAsync(Envelope envelope)
    {
        return deleteIncomingAsync(envelope);
    }

    public async Task MoveToDeadLetterStorageAsync(Envelope envelope, Exception? exception)
    {
        var dlq = new DeadLetterMessage(envelope, exception, _options.Durability.DeadLetterFingerprinter);

        if (envelope.DeliverBy.HasValue)
        {
            dlq.ExpirationTime = envelope.DeliverBy.Value;
        }
        else
        {
            dlq.ExpirationTime = DateTimeOffset.UtcNow.Add(_options.Durability.DeadLetterQueueExpiration);
        }

        await _container.UpsertItemAsync(dlq, new PartitionKey(DocumentTypes.DeadLetterPartition));

        await deleteIncomingAsync(envelope);
    }

    public async Task IncrementIncomingEnvelopeAttemptsAsync(Envelope envelope)
    {
        if (await readIncomingAsync(envelope) is not var (message, partitionKey))
        {
            // Already gone
            return;
        }

        message.Attempts = envelope.Attempts;
        await replaceIncomingAsync(message, partitionKey);
    }

    public async Task StoreIncomingAsync(Envelope envelope)
    {
        var incoming = new IncomingMessage(envelope, this);
        try
        {
            await _container.CreateItemAsync(incoming, new PartitionKey(incoming.PartitionKey));
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.Conflict)
        {
            throw new DuplicateIncomingEnvelopeException(envelope);
        }
    }

    public async Task StoreIncomingAsync(IReadOnlyList<Envelope> envelopes)
    {
        var duplicates = new List<Envelope>();
        foreach (var envelope in envelopes)
        {
            var incoming = new IncomingMessage(envelope, this);
            try
            {
                await _container.CreateItemAsync(incoming, new PartitionKey(incoming.PartitionKey));
            }
            catch (CosmosException e) when (e.StatusCode == HttpStatusCode.Conflict)
            {
                duplicates.Add(envelope);
            }
        }

        if (duplicates.Count > 0)
        {
            // GH-5123. There is no transaction across partitions, so the fresh envelopes above are already stored.
            // DurableReceiver re-runs the whole batch one envelope at a time after this exception, and storing one
            // of those a second time reads as a duplicate of itself: settled with the broker, never handled. Mark
            // what landed so that path acknowledges and handles it instead. An intra-batch copy of a fresh
            // envelope is in duplicates, so it stays unmarked and is settled as the duplicate it is.
            // By reference: Envelope equality is by id, and the copy and the original share one
            foreach (var envelope in envelopes)
            {
                if (!duplicates.Any(x => ReferenceEquals(x, envelope))) envelope.WasPersistedInInbox = true;
            }

            // Surface so DurableReceiver completes only the actual duplicates at the
            // listener and re-pipelines the fresh ones; silently swallowing would
            // route every envelope (including the duplicate) to the handler.
            throw new DuplicateIncomingEnvelopeException(duplicates);
        }
    }

    public async Task<bool> ExistsAsync(Envelope envelope, CancellationToken cancellation)
    {
        return await readIncomingAsync(envelope, cancellation) != null;
    }

    // GH-5049. Same identity as ExistsAsync, but only a document the inbox has finished with counts
    public async Task<bool> IsHandledAsync(Envelope envelope, CancellationToken cancellation)
    {
        return await readIncomingAsync(envelope, cancellation) is var (message, _) &&
               message.Status == EnvelopeStatus.Handled;
    }

    public async Task RescheduleExistingEnvelopeForRetryAsync(Envelope envelope)
    {
        envelope.Status = EnvelopeStatus.Scheduled;
        envelope.OwnerId = TransportConstants.AnyNode;

        // GH-4216. This was INSERT-only, so it threw DuplicateIncomingEnvelopeException whenever a document already
        // existed for the identity -- the normal case this method exists for (GH-2462 / GH-2823 on the relational
        // stores). Update first, insert only when nothing is there, and leave a Handled document alone: the
        // message already completed, so a retry booked after the fact is discarded
        if (await readIncomingAsync(envelope) is not var (message, _))
        {
            await StoreIncomingAsync(envelope);
            return;
        }

        if (message.Status == EnvelopeStatus.Handled)
        {
            return;
        }

        await ScheduleExecutionAsync(envelope);
    }

    public async Task MarkIncomingEnvelopeAsHandledAsync(Envelope envelope)
    {
        if (await readIncomingAsync(envelope) is not var (message, partitionKey))
        {
            // Already gone
            return;
        }

        message.Status = EnvelopeStatus.Handled;

        // GH-4784: a Handled document is kept only for idempotency and nothing recovers it, so it has
        // no owner -- the same as a document inserted already Handled (Envelope.ForPersistedHandled).
        // Every CosmosDb query that reads ownerId or drives recovery/cleanup is status-filtered, so
        // releasing the owner here cannot make a retained document look recoverable.
        message.OwnerId = TransportConstants.AnyNode;
        message.KeepUntil = DateTimeOffset.UtcNow.Add(_options.Durability.KeepAfterMessageHandling);
        await replaceIncomingAsync(message, partitionKey);
    }

    public async Task MarkIncomingEnvelopeAsHandledAsync(IReadOnlyList<Envelope> envelopes)
    {
        foreach (var envelope in envelopes)
        {
            await MarkIncomingEnvelopeAsHandledAsync(envelope);
        }
    }

    public async Task ReleaseIncomingAsync(int ownerId, Uri receivedAt)
    {
        // GH-5059. Under IdOnly a document is partitioned by its identity, not by the address it was received at, and
        // documents written before that are still in the address's partition, so the query runs across partitions.
        // Under IdAndDestination every document for the address is in its partition. Each one is replaced in the
        // partition it is actually in
        var queryText =
            "SELECT * FROM c WHERE c.docType = @docType AND c.ownerId = @ownerId AND c.receivedAt = @receivedAt";
        var query = new QueryDefinition(queryText)
            .WithParameter("@docType", DocumentTypes.Incoming)
            .WithParameter("@ownerId", ownerId)
            .WithParameter("@receivedAt", receivedAt.ToString());

        using var iterator = _container.GetItemQueryIterator<IncomingMessage>(query,
            requestOptions: _options.Durability.MessageIdentity == MessageIdentity.IdOnly
                ? null
                : new QueryRequestOptions { PartitionKey = new PartitionKey(receivedAt.ToString()) });

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            foreach (var message in response)
            {
                message.OwnerId = 0;
                await _container.ReplaceItemAsync(message, message.Id, new PartitionKey(message.PartitionKey));
            }
        }
    }

    private async Task replaceIncomingAsync(IncomingMessage message, string partitionKey)
    {
        try
        {
            await _container.ReplaceItemAsync(message, message.Id, new PartitionKey(partitionKey));
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            // Deleted since it was read. Already gone
        }
    }
}
