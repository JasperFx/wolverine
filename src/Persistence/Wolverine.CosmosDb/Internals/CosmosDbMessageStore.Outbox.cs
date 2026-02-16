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
