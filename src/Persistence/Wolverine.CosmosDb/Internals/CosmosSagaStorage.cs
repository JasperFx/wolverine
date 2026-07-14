using System.Text.Json;
using System.Text.Json.Nodes;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Azure.Cosmos;

namespace Wolverine.CosmosDb.Internals;

/// <summary>
///     GH-3415. Writes a saga document into the logical partition keyed by its own id, for applications that
///     opted into <see cref="CosmosDbConfiguration.PartitionSagasById" />.
///     <para>
///         CosmosDB takes a document's partition key from the document body, not from the request — hand it a
///         <see cref="PartitionKey" /> the body does not carry and it answers 400. A saga is the user's own POCO
///         and knows nothing about the container's <c>/partitionKey</c> path, so Wolverine has to put the value
///         there itself: serialize the saga with the CosmosClient's own serializer, add <c>partitionKey</c> = the
///         document's <c>id</c>, and write the resulting JSON. Reads and deletes need none of this — they already
///         have the id, and the id is the partition key.
///     </para>
///     <para>
///         Going through the stream API rather than <c>UpsertItemAsync&lt;T&gt;</c> is what makes that possible
///         without asking every user to add a partition key property to their saga. It costs one parse of the JSON
///         the serializer just produced, which is nothing next to the round trip that follows.
///     </para>
/// </summary>
public static class CosmosSagaStorage
{
    /// <summary>
    ///     Upsert a saga document into the partition keyed by its id. Pass the ETag captured when the saga was
    ///     read to make the write a compare-and-swap, or null for a saga being inserted for the first time.
    /// </summary>
    public static async Task UpsertAsync<T>(Container container, T saga, string? etag,
        CancellationToken cancellationToken)
    {
        var document = serialize(container, saga);
        var id = identityOf<T>(document);

        document[DocumentTypes.PartitionKeyProperty] = id;

        var options = etag.IsEmpty() ? null : new ItemRequestOptions { IfMatchEtag = etag };

        using var body = write(document);
        using var response = await container
            .UpsertItemStreamAsync(body, new PartitionKey(id), options, cancellationToken)
            .ConfigureAwait(false);

        // The stream API reports failures on the response instead of throwing. Turn them back into the
        // CosmosException the typed API would have raised, so that a 412 still reaches the saga frames'
        // catch block and becomes a SagaConcurrencyException
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    ///     Delete a saga document from the partition keyed by its id. Only used by the storage action path, where
    ///     the saga itself is in hand but its id is not — the saga chain deletes by id directly.
    /// </summary>
    public static Task DeleteAsync<T>(Container container, T saga, CancellationToken cancellationToken)
    {
        var id = identityOf<T>(serialize(container, saga));

        return container.DeleteItemAsync<T>(id, new PartitionKey(id), cancellationToken: cancellationToken);
    }

    /// <summary>
    ///     The saga as the registered CosmosClient itself would write it. Asking the client's own serializer is
    ///     the only way to know what the document really looks like: naming policies, [JsonProperty] mappings and
    ///     custom CosmosSerializer implementations all change the answer.
    /// </summary>
    private static JsonObject serialize<T>(Container container, T saga)
    {
        var serializer = container.Database.Client.ClientOptions.Serializer;
        if (serializer == null)
        {
            throw new InvalidOperationException(
                $"Cannot determine how the registered CosmosClient serializes {typeof(T).FullNameInCode()}, so Wolverine cannot write the partition key that {nameof(CosmosDbConfiguration.PartitionSagasById)}() asks for.");
        }

        using var raw = serializer.ToStream(saga);

        return JsonNode.Parse(raw) as JsonObject ??
               throw new InvalidOperationException(
                   $"The registered CosmosClient's serializer does not write {typeof(T).FullNameInCode()} as a JSON object, so it cannot be stored as a CosmosDB document.");
    }

    /// <summary>
    ///     The document id, which is also the partition to store the saga in. Guaranteed to be there by
    ///     <see cref="CosmosDbSagaSerializationValidator" />, which refuses at startup any saga the registered
    ///     client would not write an "id" for (GH-3416).
    /// </summary>
    private static string identityOf<T>(JsonObject document)
    {
        var id = document[CosmosSagaIdentity.CosmosIdProperty] is JsonValue value &&
                 value.TryGetValue<string>(out var identity)
            ? identity
            : null;

        if (id.IsEmpty())
        {
            throw new InvalidOperationException(
                $"The registered CosmosClient's serializer does not write a '{CosmosSagaIdentity.CosmosIdProperty}' property for {typeof(T).FullNameInCode()}, so the saga has no identity to partition by.");
        }

        return id!;
    }

    private static MemoryStream write(JsonObject document)
    {
        var stream = new MemoryStream();

        using (var writer = new Utf8JsonWriter(stream))
        {
            document.WriteTo(writer);
        }

        stream.Position = 0;

        return stream;
    }
}
