using Microsoft.Azure.Cosmos;
using Wolverine.Logging;
using Wolverine.Persistence.Durability;

namespace Wolverine.CosmosDb.Internals;

public partial class CosmosDbMessageStore : IMessageStoreAdmin
{
    public Task DeleteAllHandledAsync()
    {
        throw new NotSupportedException("This function is not yet supported by CosmosDb");
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
        var containerProperties = new ContainerProperties(DocumentTypes.ContainerName, DocumentTypes.PartitionKeyPath);
        await database.CreateContainerIfNotExistsAsync(containerProperties);
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
