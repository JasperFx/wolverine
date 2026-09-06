using System.Net;
using Microsoft.Azure.Cosmos;
using Wolverine.Persistence.Durability;

namespace Wolverine.CosmosDb.Internals;

public partial class CosmosDbMessageStore : IMessageOutbox
{
    public async Task<IReadOnlyList<Envelope>> LoadOutgoingAsync(Uri destination)
    {
        var partitionKey = destination.ToString();
        var queryText =
            "SELECT * FROM c WHERE c.docType = @docType AND c.destination = @destination";
        var query = new QueryDefinition(queryText)
            .WithParameter("@docType", DocumentTypes.Outgoing)
            .WithParameter("@destination", destination.ToString());

        var results = new List<Envelope>();
        using var iterator = _container.GetItemQueryIterator<OutgoingMessage>(query,
            requestOptions: new QueryRequestOptions
            {
                PartitionKey = new PartitionKey(partitionKey)
            });

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            results.AddRange(response.Select(x => x.Read()));
        }

        return results;
    }

    public async Task StoreOutgoingAsync(Envelope envelope, int ownerId)
    {
        var outgoing = new OutgoingMessage(envelope)
        {
            OwnerId = ownerId
        };

        await _container.UpsertItemAsync(outgoing, new PartitionKey(outgoing.PartitionKey));
    }

    // Azure Cosmos DB's own limits on a TransactionalBatch: at most 100 operations, and 2MB of
    // payload. The operation cap is not the binding one here -- an Envelope body can be 100KB, so a
    // full batch of 100 would be 10MB. The byte budget is deliberately under the documented 2MB to
    // leave room for the JSON envelope around each body.
    private const int MaximumBatchOperations = 100;
    private const int MaximumBatchBytes = 1_500_000;

    /// <summary>
    /// GH-4369. A TransactionalBatch is ONE request to Cosmos instead of one per envelope, which is
    /// the round trip that costs most on this store -- every upsert is a network call with its own RU
    /// charge.
    ///
    /// <para>
    /// A transactional batch must be single-partition, so this groups by partition key rather than
    /// assuming one. In practice a coalesced batch comes from a single <c>DurableSendingAgent</c> and
    /// therefore a single destination, which IS the partition key -- but "in practice" is not a
    /// constraint the storage layer gets to rely on.
    /// </para>
    ///
    /// <para>
    /// Both Cosmos batch limits are enforced up front rather than discovered as a 413 at runtime. A
    /// batch that fails for any other reason surfaces to the caller, whose coalescer retries each
    /// envelope on its own -- and an upsert is idempotent, so a partially-observed failure cannot
    /// double-write.
    /// </para>
    /// </summary>
    public async Task StoreOutgoingAsync(IReadOnlyList<Envelope> envelopes, int ownerId)
    {
        if (envelopes.Count == 0) return;

        if (envelopes.Count == 1)
        {
            await StoreOutgoingAsync(envelopes[0], ownerId);
            return;
        }

        var messages = new List<OutgoingMessage>(envelopes.Count);
        foreach (var envelope in envelopes)
        {
            messages.Add(new OutgoingMessage(envelope) { OwnerId = ownerId });
        }

        foreach (var partition in messages.GroupBy(x => x.PartitionKey))
        {
            foreach (var chunk in chunkForBatching(partition))
            {
                if (chunk.Count == 1)
                {
                    await _container.UpsertItemAsync(chunk[0], new PartitionKey(chunk[0].PartitionKey));
                    continue;
                }

                var batch = _container.CreateTransactionalBatch(new PartitionKey(partition.Key));
                foreach (var message in chunk)
                {
                    batch.UpsertItem(message);
                }

                using var response = await batch.ExecuteAsync();
                if (!response.IsSuccessStatusCode)
                {
                    throw new CosmosException(
                        $"Failed to store a batch of {chunk.Count} outgoing envelopes at partition {partition.Key}",
                        response.StatusCode, 0, response.ActivityId, response.RequestCharge);
                }
            }
        }
    }

    /// <summary>
    /// Split one partition's messages into batches that respect BOTH Cosmos limits. A single message
    /// larger than the byte budget goes out on its own and is left to the upsert path, which has no
    /// batch limit to breach.
    /// </summary>
    private static IEnumerable<List<OutgoingMessage>> chunkForBatching(IEnumerable<OutgoingMessage> messages)
    {
        var current = new List<OutgoingMessage>();
        var bytes = 0L;

        foreach (var message in messages)
        {
            var size = message.Body?.Length ?? 0;

            if (current.Count > 0 && (current.Count == MaximumBatchOperations || bytes + size > MaximumBatchBytes))
            {
                yield return current;
                current = new List<OutgoingMessage>();
                bytes = 0;
            }

            current.Add(message);
            bytes += size;
        }

        if (current.Count > 0)
        {
            yield return current;
        }
    }

    public async Task DeleteOutgoingAsync(Envelope[] envelopes)
    {
        foreach (var envelope in envelopes)
        {
            await DeleteOutgoingAsync(envelope);
        }
    }

    public async Task DeleteOutgoingAsync(Envelope envelope)
    {
        var id = $"outgoing|{envelope.Id}";
        var partitionKey = envelope.Destination?.ToString() ?? DocumentTypes.SystemPartition;
        try
        {
            await _container.DeleteItemAsync<OutgoingMessage>(id, new PartitionKey(partitionKey));
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            // Already gone
        }
    }

    public async Task DiscardAndReassignOutgoingAsync(Envelope[] discards, Envelope[] reassigned, int nodeId)
    {
        foreach (var discard in discards)
        {
            await DeleteOutgoingAsync(discard);
        }

        foreach (var envelope in reassigned)
        {
            var id = $"outgoing|{envelope.Id}";
            var partitionKey = envelope.Destination?.ToString() ?? DocumentTypes.SystemPartition;
            try
            {
                var response =
                    await _container.ReadItemAsync<OutgoingMessage>(id, new PartitionKey(partitionKey));
                var message = response.Resource;
                message.OwnerId = nodeId;
                await _container.ReplaceItemAsync(message, id, new PartitionKey(partitionKey));
            }
            catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
            {
                // Already gone
            }
        }
    }
}
