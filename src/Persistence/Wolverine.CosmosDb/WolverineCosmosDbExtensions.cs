using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.CosmosDb.Internals;
using Wolverine.Persistence.Durability;
using Wolverine.Persistence.Sagas;

namespace Wolverine.CosmosDb;

public static class WolverineCosmosDbExtensions
{
    /// <summary>
    /// Utilize CosmosDb for envelope and saga storage with this system.
    /// Requires a CosmosClient and database name to be configured.
    /// </summary>
    /// <param name="options"></param>
    /// <param name="databaseName">The CosmosDB database name to use</param>
    /// <returns></returns>
    public static WolverineOptions UseCosmosDbPersistence(this WolverineOptions options, string databaseName)
    {
        options.Services.AddSingleton<IMessageStore>(sp =>
        {
            var client = sp.GetRequiredService<CosmosClient>();
            var container = client.GetDatabase(databaseName).GetContainer(DocumentTypes.ContainerName);
            var wolverineOptions = sp.GetRequiredService<WolverineOptions>();
            return new CosmosDbMessageStore(client, databaseName, container, wolverineOptions);
        });

        // Register the CosmosDB Container for use by code-generated handlers
        options.Services.AddSingleton(sp =>
        {
            var client = sp.GetRequiredService<CosmosClient>();
            return client.GetDatabase(databaseName).GetContainer(DocumentTypes.ContainerName);
        });

        options.CodeGeneration.InsertFirstPersistenceStrategy<CosmosDbPersistenceFrameProvider>();
        options.CodeGeneration.ReferenceAssembly(typeof(WolverineCosmosDbExtensions).Assembly);
        return options;
    }
}
