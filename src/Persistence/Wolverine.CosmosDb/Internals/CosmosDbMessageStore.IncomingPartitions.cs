using System.Net;
using Microsoft.Azure.Cosmos;

namespace Wolverine.CosmosDb.Internals;

public partial class CosmosDbMessageStore
{
    /// <summary>
    ///     GH-5059. The partition an inbox document is written to. Cosmos DB only enforces a document id as unique
    ///     within one partition, so the partition has to follow the message identity: under
    ///     <see cref="MessageIdentity.IdOnly" /> the same id received at another address is the same message, and
    ///     partitioning by destination stored it twice instead of rejecting it as a duplicate. Under IdOnly the document
    ///     is partitioned by its identity (which also spreads the inbox evenly); under
    ///     <see cref="MessageIdentity.IdAndDestination" /> the destination is part of the identity, so it stays the
    ///     partition
    /// </summary>
    public string IncomingPartitionKeyFor(Envelope envelope)
    {
        return _options.Durability.MessageIdentity == MessageIdentity.IdOnly
            ? _identity(envelope)
            : destinationPartitionKeyFor(envelope);
    }

    private static string destinationPartitionKeyFor(Envelope envelope)
    {
        return envelope.Destination?.ToString() ?? DocumentTypes.SystemPartition;
    }

    // Where an existing inbox document can be: the partition it is written to now and, under IdOnly, the destination
    // partition every inbox document was written to before GH-5059, so a document stored by an earlier version is
    // still found, updated and deleted
    private IEnumerable<string> incomingPartitionKeysFor(Envelope envelope)
    {
        var current = IncomingPartitionKeyFor(envelope);
        yield return current;

        var legacy = destinationPartitionKeyFor(envelope);
        if (legacy != current)
        {
            yield return legacy;
        }
    }

    /// <summary>
    ///     Point read of the envelope's inbox document wherever it is, with the partition it was found in. Null when
    ///     there is none
    /// </summary>
    private async Task<(IncomingMessage Message, string PartitionKey)?> readIncomingAsync(Envelope envelope,
        CancellationToken cancellation = default)
    {
        var id = _identity(envelope);
        foreach (var partitionKey in incomingPartitionKeysFor(envelope))
        {
            try
            {
                var response = await _container.ReadItemAsync<IncomingMessage>(id, new PartitionKey(partitionKey),
                    cancellationToken: cancellation);
                return (response.Resource, partitionKey);
            }
            catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
            {
                // Try the next partition it could be in
            }
        }

        return null;
    }

    // Deletes the envelope's inbox document wherever it is. Already gone is fine
    private async Task deleteIncomingAsync(Envelope envelope)
    {
        var id = _identity(envelope);
        foreach (var partitionKey in incomingPartitionKeysFor(envelope))
        {
            try
            {
                await _container.DeleteItemAsync<IncomingMessage>(id, new PartitionKey(partitionKey));
                return;
            }
            catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
            {
                // Try the next partition it could be in
            }
        }
    }
}
