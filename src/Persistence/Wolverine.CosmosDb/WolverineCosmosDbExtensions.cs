using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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
        return options.UseCosmosDbPersistence(databaseName, _ => { });
    }

    /// <summary>
    /// Utilize CosmosDb for envelope and saga storage with this system.
    /// Requires a CosmosClient and database name to be configured.
    /// </summary>
    /// <param name="options"></param>
    /// <param name="databaseName">The CosmosDB database name to use</param>
    /// <param name="configure">Optional configuration of the CosmosDB integration itself</param>
    /// <returns></returns>
    public static WolverineOptions UseCosmosDbPersistence(this WolverineOptions options, string databaseName,
        Action<CosmosDbConfiguration> configure)
    {
        var configuration = new CosmosDbConfiguration();
        configure(configuration);

        // Read back by CosmosDbPersistenceFrameProvider when the saga frames are generated
        options.Services.AddSingleton(configuration);

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

        // GH-3416 -- CosmosDB requires a lowercase "id" on every document. A saga's PascalCase Id only
        // serializes that way if the CosmosClient is set to camel case its property names, and nothing
        // else in the system says so until the first saga write comes back a 400. Refuse at host start,
        // where the client is resolvable and the saga types are known.
        options.Services.AddSingleton<IHostedService, CosmosDbSagaSerializationValidator>();

        options.CodeGeneration.InsertFirstPersistenceStrategy<CosmosDbPersistenceFrameProvider>();
        options.CodeGeneration.ReferenceAssembly(typeof(WolverineCosmosDbExtensions).Assembly);
        return options;
    }
}
