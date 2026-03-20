using System.Net;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Azure.Cosmos;
using Wolverine;
using Wolverine.CosmosDb;
using Wolverine.CosmosDb.Internals;
using Wolverine.Persistence.Durability;

namespace CosmosDbTests;

public class AppFixture : IAsyncLifetime
{
    public const string AccountKey = "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==";
    public const string DatabaseName = "wolverine_tests";

    private IContainer? _cosmosContainer;

    public string ConnectionString { get; private set; } =
        "AccountEndpoint=https://localhost:8081/;AccountKey=C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==";

    public CosmosClient Client { get; private set; } = null!;
    public Container Container { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        _cosmosContainer = new ContainerBuilder()
            .WithImage("mcr.microsoft.com/cosmosdb/linux/azure-cosmos-emulator:vnext-preview")
            .WithPortBinding(8081, true)
            .WithPortBinding(1234, true)
            .WithEnvironment("PROTOCOL", "https")
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilMessageIsLogged("Now listening on"))
            .Build();

        await _cosmosContainer.StartAsync();

        var host = _cosmosContainer.Hostname;
        var port = _cosmosContainer.GetMappedPublicPort(8081);
        ConnectionString = $"AccountEndpoint=https://{host}:{port}/;AccountKey={AccountKey}";

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

        if (_cosmosContainer != null)
        {
            await _cosmosContainer.DisposeAsync();
        }
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
