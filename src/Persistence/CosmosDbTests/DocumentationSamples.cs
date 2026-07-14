using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Hosting;
using Newtonsoft.Json.Linq;
using Wolverine;
using Wolverine.CosmosDb;

namespace CosmosDbTests;

public class DocumentationSamples
{
    public static void PartitionSagasById()
    {
        using var host = Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                #region sample_cosmos_partition_sagas_by_id

                opts.UseCosmosDbPersistence("your-database-name", cosmos =>
                {
                    // Each saga document gets its own logical partition, keyed by the saga id
                    cosmos.PartitionSagasById();
                });

                #endregion
            }).Build();
    }

    #region sample_cosmos_migrate_saga_to_its_own_partition

    public static async Task MigrateSagaAsync(Container container, string sagaId)
    {
        // The legacy copy, in the undefined partition
        var response = await container.ReadItemAsync<JObject>(sagaId, PartitionKey.None);
        var document = response.Resource;

        document["partitionKey"] = sagaId;

        await container.UpsertItemAsync(document, new PartitionKey(sagaId));
        await container.DeleteItemAsync<JObject>(sagaId, PartitionKey.None,
            new ItemRequestOptions { IfMatchEtag = response.ETag });
    }

    #endregion
}
