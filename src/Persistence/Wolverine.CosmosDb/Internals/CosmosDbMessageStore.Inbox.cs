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
        foreach (var envelope in envelopes)
        {
            var incoming = new IncomingMessage(envelope, this);
            try
            {
                await _container.CreateItemAsync(incoming, new PartitionKey(incoming.PartitionKey));
            }
            catch (CosmosException e) when (e.StatusCode == HttpStatusCode.Conflict)
            {
                // Skip duplicates in batch mode; DurableReceiver will retry one at a time
            }
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

    public Task RescheduleExistingEnvelopeForRetryAsync(Envelope envelope)
    {
        envelope.Status = EnvelopeStatus.Scheduled;
        envelope.OwnerId = TransportConstants.AnyNode;

        return StoreIncomingAsync(envelope);
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
