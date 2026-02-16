using Microsoft.Azure.Cosmos;

namespace Wolverine.CosmosDb;

public interface ICosmosDbOp : ISideEffect
{
    Task Execute(Container container);
}

public class StoreDoc<T>(T Document) : ICosmosDbOp
{
    public async Task Execute(Container container)
    {
        await container.UpsertItemAsync(Document);
    }
}

public class DeleteById(string Id, string PartitionKeyValue) : ICosmosDbOp
{
    public async Task Execute(Container container)
    {
        try
        {
            await container.DeleteItemAsync<dynamic>(Id, new PartitionKey(PartitionKeyValue));
        }
        catch (CosmosException e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Already gone
        }
    }
}

/// <summary>
/// Side effect helper class for Wolverine's integration with CosmosDb
/// </summary>
public static class CosmosDbOps
{
    /// <summary>
    /// Store (upsert) a CosmosDb document
    /// </summary>
    /// <param name="document"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static ICosmosDbOp Store<T>(T document) => new StoreDoc<T>(document);

    /// <summary>
    /// Delete a CosmosDb document by its id and partition key
    /// </summary>
    /// <param name="id"></param>
    /// <param name="partitionKey"></param>
    /// <returns></returns>
    public static ICosmosDbOp Delete(string id, string partitionKey) => new DeleteById(id, partitionKey);
}
