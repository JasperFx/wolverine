using System.Net;
using Microsoft.Azure.Cosmos;
using Wolverine.Persistence.Durability;
using Wolverine.Transports;

namespace Wolverine.CosmosDb.Internals;

public partial class CosmosDbMessageStore : IMessageInbox
{
    public async Task ScheduleExecutionAsync(Envelope envelope)
    {
        var id = _identity(envelope);
        var partitionKey = envelope.Destination?.ToString() ?? DocumentTypes.SystemPartition;
        try
        {
            var response =
                await _container.ReadItemAsync<IncomingMessage>(id, new PartitionKey(partitionKey));
            var message = response.Resource;

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
            await _container.ReplaceItemAsync(message, id, new PartitionKey(partitionKey));
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            // Already gone
        }
    }

    public async Task DeleteIncomingEnvelopeAsync(Envelope envelope)
    {
        var partitionKey = envelope.Destination?.ToString() ?? DocumentTypes.SystemPartition;

        try
        {
            await _container.DeleteItemAsync<IncomingMessage>(_identity(envelope), new PartitionKey(partitionKey));
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            // Already gone
        }
    }

    public async Task MoveToDeadLetterStorageAsync(Envelope envelope, Exception? exception)
    {
        var id = _identity(envelope);
        var partitionKey = envelope.Destination?.ToString() ?? DocumentTypes.SystemPartition;

        var dlq = new DeadLetterMessage(envelope, exception);

        if (envelope.DeliverBy.HasValue)
        {
            dlq.ExpirationTime = envelope.DeliverBy.Value;
        }
        else
        {
            dlq.ExpirationTime = DateTimeOffset.UtcNow.Add(_options.Durability.DeadLetterQueueExpiration);
        }

        await _container.UpsertItemAsync(dlq, new PartitionKey(DocumentTypes.DeadLetterPartition));

        try
        {
            await _container.DeleteItemAsync<IncomingMessage>(id, new PartitionKey(partitionKey));
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            // Already gone
        }
    }

    public async Task IncrementIncomingEnvelopeAttemptsAsync(Envelope envelope)
    {
        var id = _identity(envelope);
        var partitionKey = envelope.Destination?.ToString() ?? DocumentTypes.SystemPartition;
        try
        {
            var response =
                await _container.ReadItemAsync<IncomingMessage>(id, new PartitionKey(partitionKey));
            var message = response.Resource;
            message.Attempts = envelope.Attempts;
            await _container.ReplaceItemAsync(message, id, new PartitionKey(partitionKey));
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            // Already gone
        }
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
            // Surface so DurableReceiver completes only the actual duplicates at the
            // listener and re-pipelines the fresh ones; silently swallowing would
            // route every envelope (including the duplicate) to the handler.
            throw new DuplicateIncomingEnvelopeException(duplicates);
        }
    }

    public async Task<bool> ExistsAsync(Envelope envelope, CancellationToken cancellation)
    {
        var id = IdentityFor(envelope);
        var partitionKey = envelope.Destination?.ToString() ?? DocumentTypes.SystemPartition;
        try
        {
            await _container.ReadItemAsync<IncomingMessage>(id, new PartitionKey(partitionKey),
                cancellationToken: cancellation);
            return true;
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    public async Task RescheduleExistingEnvelopeForRetryAsync(Envelope envelope)
    {
        envelope.Status = EnvelopeStatus.Scheduled;
        envelope.OwnerId = TransportConstants.AnyNode;

        // GH-4216. This was INSERT-only, so it threw DuplicateIncomingEnvelopeException whenever a document already
        // existed for the identity -- the normal case this method exists for (GH-2462 / GH-2823 on the relational
        // stores). Update first, insert only when nothing is there, and leave a Handled document alone: the
        // message already completed, so a retry booked after the fact is discarded
        var id = _identity(envelope);
        var partitionKey = envelope.Destination?.ToString() ?? DocumentTypes.SystemPartition;
        try
        {
            var response = await _container.ReadItemAsync<IncomingMessage>(id, new PartitionKey(partitionKey));
            if (response.Resource.Status == EnvelopeStatus.Handled)
            {
                return;
            }
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            await StoreIncomingAsync(envelope);
            return;
        }

        await ScheduleExecutionAsync(envelope);
    }

    public async Task MarkIncomingEnvelopeAsHandledAsync(Envelope envelope)
    {
        var id = _identity(envelope);
        var partitionKey = envelope.Destination?.ToString() ?? DocumentTypes.SystemPartition;
        try
        {
            var response =
                await _container.ReadItemAsync<IncomingMessage>(id, new PartitionKey(partitionKey));
            var message = response.Resource;
            message.Status = EnvelopeStatus.Handled;

            // GH-4784: a Handled document is kept only for idempotency and nothing recovers it, so it has
            // no owner -- the same as a document inserted already Handled (Envelope.ForPersistedHandled).
            // Every CosmosDb query that reads ownerId or drives recovery/cleanup is status-filtered, so
            // releasing the owner here cannot make a retained document look recoverable.
            message.OwnerId = TransportConstants.AnyNode;
            message.KeepUntil = DateTimeOffset.UtcNow.Add(_options.Durability.KeepAfterMessageHandling);
            await _container.ReplaceItemAsync(message, id, new PartitionKey(partitionKey));
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            // Already gone
        }
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
        var partitionKey = receivedAt.ToString();
        var queryText =
            "SELECT * FROM c WHERE c.docType = @docType AND c.ownerId = @ownerId AND c.receivedAt = @receivedAt";
        var query = new QueryDefinition(queryText)
            .WithParameter("@docType", DocumentTypes.Incoming)
            .WithParameter("@ownerId", ownerId)
            .WithParameter("@receivedAt", receivedAt.ToString());

        using var iterator = _container.GetItemQueryIterator<IncomingMessage>(query,
            requestOptions: new QueryRequestOptions
            {
                PartitionKey = new PartitionKey(partitionKey)
            });

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            foreach (var message in response)
            {
                message.OwnerId = 0;
                await _container.ReplaceItemAsync(message, message.Id, new PartitionKey(partitionKey));
            }
        }
    }
}
