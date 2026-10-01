using Microsoft.Azure.Cosmos;
using Wolverine.Logging;
using Wolverine.Persistence.Durability;

namespace Wolverine.CosmosDb.Internals;

public partial class CosmosDbMessageStore : IMessageStoreAdmin
{
    /// <summary>
    /// GH-4509. Used to throw <c>NotSupportedException</c>, so the built-in <c>clear-handled</c> command
    /// failed outright on this provider — leaving no way to clean up after the fact, on the one store where
    /// handled envelopes were never swept in the first place.
    /// </summary>
    /// <remarks>
    /// The same query the durability agent's <c>tryDeleteExpiredHandledEnvelopes</c> runs, minus the
    /// <c>keepUntil</c> predicate: this is the deliberate "clear them all now" verb, not the timed sweep, so
    /// it is neither batched nor bounded.
    /// </remarks>
    public async Task DeleteAllHandledAsync()
    {
        var query = new QueryDefinition(
                "SELECT c.id, c.partitionKey FROM c WHERE c.docType = @docType AND c.status = @status")
            .WithParameter("@docType", DocumentTypes.Incoming)
            .WithParameter("@status", EnvelopeStatus.Handled);

        using var iterator = _container.GetItemQueryIterator<dynamic>(query);

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            foreach (var item in response)
            {
                string id = item.id;
                string partitionKey = item.partitionKey;

                try
                {
                    await _container.DeleteItemAsync<dynamic>(id, new PartitionKey(partitionKey));
                }
                catch (CosmosException)
                {
                    // Best effort, matching DeleteByDocTypeAsync
                }
            }
        }
    }

    public async Task ClearAllAsync()
    {
        await DeleteByDocTypeAsync(DocumentTypes.Incoming);
        await DeleteByDocTypeAsync(DocumentTypes.Outgoing);
        await DeleteByDocTypeAsync(DocumentTypes.DeadLetter);
        await DeleteByDocTypeAsync(DocumentTypes.Node);
        await DeleteByDocTypeAsync(DocumentTypes.AgentAssignment);
        await DeleteByDocTypeAsync(DocumentTypes.Lock);
        await DeleteByDocTypeAsync(DocumentTypes.NodeRecord);
        await DeleteByDocTypeAsync(DocumentTypes.AgentRestriction);
        await DeleteByDocTypeAsync(DocumentTypes.NodeSequence);
    }

    public Task RebuildAsync()
    {
        return ClearAllAsync();
    }

    public async Task<PersistedCounts> FetchCountsAsync()
    {
        var counts = new PersistedCounts();

        counts.DeadLetter = await CountByQueryAsync(
            "SELECT VALUE COUNT(1) FROM c WHERE c.docType = @docType",
            DocumentTypes.DeadLetter);

        counts.Handled = await CountByQueryAsync(
            "SELECT VALUE COUNT(1) FROM c WHERE c.docType = @docType AND c.status = @status",
            DocumentTypes.Incoming, ("@status", EnvelopeStatus.Handled));

        counts.Incoming = await CountByQueryAsync(
            "SELECT VALUE COUNT(1) FROM c WHERE c.docType = @docType AND c.status = @status",
            DocumentTypes.Incoming, ("@status", EnvelopeStatus.Incoming));

        counts.Outgoing = await CountByQueryAsync(
            "SELECT VALUE COUNT(1) FROM c WHERE c.docType = @docType",
            DocumentTypes.Outgoing);

        counts.Scheduled = await CountByQueryAsync(
            "SELECT VALUE COUNT(1) FROM c WHERE c.docType = @docType AND c.status = @status",
            DocumentTypes.Incoming, ("@status", EnvelopeStatus.Scheduled));

        return counts;
    }

    public async Task<IReadOnlyList<Envelope>> AllIncomingAsync()
    {
        var queryText = "SELECT * FROM c WHERE c.docType = @docType";
        var query = new QueryDefinition(queryText)
            .WithParameter("@docType", DocumentTypes.Incoming);

        var results = new List<Envelope>();
        using var iterator = _container.GetItemQueryIterator<IncomingMessage>(query);

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            results.AddRange(response.Select(m => m.Read()));
        }

        return results;
    }

    public async Task<IReadOnlyList<Envelope>> AllOutgoingAsync()
    {
        var queryText = "SELECT * FROM c WHERE c.docType = @docType";
        var query = new QueryDefinition(queryText)
            .WithParameter("@docType", DocumentTypes.Outgoing);

        var results = new List<Envelope>();
        using var iterator = _container.GetItemQueryIterator<OutgoingMessage>(query);

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            results.AddRange(response.Select(m => m.Read()));
        }

        return results;
    }

    public async Task ReleaseAllOwnershipAsync()
    {
        await ReleaseOwnershipByDocTypeAsync(DocumentTypes.Incoming);
        await ReleaseOwnershipByDocTypeAsync(DocumentTypes.Outgoing);
    }

    public async Task ReleaseAllOwnershipAsync(int ownerId)
    {
        await ReleaseOwnershipByDocTypeAsync(DocumentTypes.Incoming, ownerId);
        await ReleaseOwnershipByDocTypeAsync(DocumentTypes.Outgoing, ownerId);
    }

    public async Task CheckConnectivityAsync(CancellationToken token)
    {
        var query = new QueryDefinition("SELECT VALUE COUNT(1) FROM c WHERE c.docType = @docType")
            .WithParameter("@docType", DocumentTypes.Incoming);
        using var iterator = _container.GetItemQueryIterator<int>(query);
        await iterator.ReadNextAsync(token);
    }

    public async Task MigrateAsync()
    {
        var database = _client.GetDatabase(_databaseName);

        // The store's own container rather than the default name, so that a container configured through
        // CosmosDbConfiguration.UseContainer() is the one created
        var containerProperties = new ContainerProperties(_container.Id, DocumentTypes.PartitionKeyPath);

        try
        {
            await database.CreateContainerIfNotExistsAsync(containerProperties);
        }
        catch (ArgumentException e)
        {
            // CosmosDbConfiguration.UseContainer() exists to aim Wolverine at a container inside a database
            // shared with other things, which makes "I pointed it at a container I already had, partitioned on
            // /tenantId" a realistic mistake in a way it never was while the name was fixed at "wolverine". The
            // SDK does catch it -- it reads the container before deciding to create one, and will not hand back
            // one whose partition key path is not the requested one -- but it refuses with an ArgumentException
            // that says only that the two paths differ, leaving the user no way out of a property that is fixed
            // at container creation and cannot be migrated. Say what to do about it instead.
            var existing = await tryReadPartitionKeyPathAsync(database);
            if (existing != null && existing != DocumentTypes.PartitionKeyPath)
            {
                throw new InvalidOperationException(
                    $"The existing CosmosDB container '{_container.Id}' in database '{_databaseName}' is partitioned on '{existing}', but Wolverine requires '{DocumentTypes.PartitionKeyPath}' -- it stamps that property onto every envelope, saga and node document and passes the value explicitly on each write. A container's partition key path cannot be changed after it is created and Wolverine cannot repartition it, so either point CosmosDbConfiguration.UseContainer() at a container name of its own, or let Wolverine create the container itself.",
                    e);
            }

            throw;
        }
    }

    /// <summary>
    /// The partition key path of the store's container as it exists right now, or null when it cannot be read --
    /// only ever called off the failure path above, where one more round trip costs nothing because the host is
    /// about to refuse to start anyway.
    /// </summary>
    private async Task<string?> tryReadPartitionKeyPathAsync(Database database)
    {
        try
        {
            var response = await database.GetContainer(_container.Id).ReadContainerAsync();
            return response.Resource.PartitionKeyPath;
        }
        catch (CosmosException)
        {
            return null;
        }
    }

    private async Task<int> CountByQueryAsync(string queryText, string docType,
        params (string name, object value)[] extraParams)
    {
        var query = new QueryDefinition(queryText)
            .WithParameter("@docType", docType);
        foreach (var (name, value) in extraParams)
        {
            query = query.WithParameter(name, value);
        }

        using var iterator = _container.GetItemQueryIterator<int>(query);
        if (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            return response.FirstOrDefault();
        }

        return 0;
    }

    private async Task DeleteByDocTypeAsync(string docType)
    {
        var queryText = "SELECT c.id, c.partitionKey FROM c WHERE c.docType = @docType";
        var query = new QueryDefinition(queryText)
            .WithParameter("@docType", docType);

        using var iterator = _container.GetItemQueryIterator<dynamic>(query);

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            foreach (var item in response)
            {
                string id = item.id;
                string pk = item.partitionKey;
                try
                {
                    await _container.DeleteItemAsync<dynamic>(id, new PartitionKey(pk));
                }
                catch (CosmosException)
                {
                    // Best effort
                }
            }
        }
    }

    private async Task ReleaseOwnershipByDocTypeAsync(string docType, int? ownerId = null)
    {
        var queryText = ownerId.HasValue
            ? "SELECT * FROM c WHERE c.docType = @docType AND c.ownerId = @ownerId"
            : "SELECT * FROM c WHERE c.docType = @docType AND c.ownerId != 0";

        var query = new QueryDefinition(queryText)
            .WithParameter("@docType", docType);

        if (ownerId.HasValue)
        {
            query = query.WithParameter("@ownerId", ownerId.Value);
        }

        using var iterator = _container.GetItemQueryIterator<dynamic>(query);

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            foreach (var item in response)
            {
                string id = item.id;
                string pk = item.partitionKey;
                item.ownerId = 0;
                try
                {
                    await _container.ReplaceItemAsync<dynamic>(item, id, new PartitionKey(pk));
                }
                catch (CosmosException)
                {
                    // Best effort
                }
            }
        }
    }
}
