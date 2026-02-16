using System.Net;
using Microsoft.Azure.Cosmos;
using Wolverine;
using Wolverine.CosmosDb;
using Wolverine.CosmosDb.Internals;
using Wolverine.Persistence.Durability;

namespace CosmosDbTests;

public class AppFixture : IAsyncLifetime
{
    // CosmosDB Linux emulator defaults
    public const string ConnectionString =
        "AccountEndpoint=https://localhost:8081/;AccountKey=C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==";

    public const string DatabaseName = "wolverine_tests";

    public CosmosClient Client { get; private set; } = null!;
    public Container Container { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var clientOptions = new CosmosClientOptions
        {
            HttpClientFactory = () =>
            {
                HttpMessageHandler httpMessageHandler = new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback =
                        HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
                };
                return new HttpClient(httpMessageHandler);
            },
            ConnectionMode = ConnectionMode.Gateway,
            SerializerOptions = new CosmosSerializationOptions
            {
                PropertyNamingPolicy = CosmosPropertyNamingPolicy.CamelCase
            }
        };

        Client = new CosmosClient(ConnectionString, clientOptions);

        // Retry database/container creation since the vnext emulator can be slow to initialize
        for (var attempt = 1; attempt <= 10; attempt++)
        {
            try
            {
                var databaseResponse = await Client.CreateDatabaseIfNotExistsAsync(DatabaseName);
                var containerProperties =
                    new ContainerProperties(DocumentTypes.ContainerName, DocumentTypes.PartitionKeyPath);
                var containerResponse =
                    await databaseResponse.Database.CreateContainerIfNotExistsAsync(containerProperties);
                Container = containerResponse.Container;
                return;
            }
            catch (CosmosException e) when (e.StatusCode == HttpStatusCode.ServiceUnavailable ||
                                            e.StatusCode == HttpStatusCode.InternalServerError)
            {
                if (attempt == 10) throw;
                await Task.Delay(TimeSpan.FromSeconds(3));
            }
        }
    }

    public async Task DisposeAsync()
    {
        Client?.Dispose();
    }

    public CosmosDbMessageStore BuildMessageStore()
    {
        return new CosmosDbMessageStore(Client, DatabaseName, Container, new WolverineOptions());
    }

    public async Task ClearAll()
    {
        var store = BuildMessageStore();
        await store.Admin.ClearAllAsync();
    }
}

[CollectionDefinition("cosmosdb")]
public class CosmosDbCollection : ICollectionFixture<AppFixture>
{
}
