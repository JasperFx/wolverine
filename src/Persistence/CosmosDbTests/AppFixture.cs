using Microsoft.Azure.Cosmos;
using System.Net;
using Testcontainers.CosmosDb;
using Wolverine;
using Wolverine.CosmosDb.Internals;

namespace CosmosDbTests;

public class AppFixture : IAsyncLifetime
{
    public const string DatabaseName = "wolverine_tests";
    // Pinned by digest, not the floating vnext-latest tag. The emulator image rebuilt on 2026-10-08 began
    // returning a binary-JSON node type that Microsoft.Azure.Cosmos before ~3.63 cannot parse ("Unknown
    // JsonNodeType: Unknown"); every query failed, no node could load cluster state, and CICosmosDb hung on
    // every run until its job cap -- while a developer machine kept passing on its cached older image under
    // the same tag. A pinned digest is the same on every machine. Move it deliberately: re-run CICosmosDb
    // against the new digest before updating this. This is the multi-arch manifest list (amd64 + arm64).
    private const string CosmosDbImage =
        "mcr.microsoft.com/cosmosdb/linux/azure-cosmos-emulator@sha256:4535ddbd27bba762f3e068f7585feb0022a2559ceccc9394b23aa38d91d77c38";

    // Static container shared across all AppFixture instances
    private static CosmosDbContainer _sharedContainer = null!;
    private static string _sharedConnectionString = null!;
    private static readonly SemaphoreSlim _lock = new(1, 1);
    public static string ConnectionString => _sharedConnectionString;

    public CosmosClient Client { get; private set; } = null!;
    public Container Container { get; private set; } = null!;

    private static async Task EnsureContainerStarted()
    {
        await _lock.WaitAsync();
        try
        {
            if (_sharedContainer != null) return;

            _sharedContainer = new CosmosDbBuilder(CosmosDbImage)
                .Build();

            await _sharedContainer.StartAsync();
            _sharedConnectionString = _sharedContainer.GetConnectionString();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask InitializeAsync()
    {
        await EnsureContainerStarted();

        var clientOptions = new CosmosClientOptions
        {
            HttpClientFactory = () =>
            {
                var handler = new HttpClientHandler();
                var port = _sharedContainer.GetMappedPublicPort(8081);
                return new HttpClient(new FixRequestLocationHandler(port, handler));
            },
            ConnectionMode = ConnectionMode.Gateway,
            SerializerOptions = new CosmosSerializationOptions
            {
                PropertyNamingPolicy = CosmosPropertyNamingPolicy.CamelCase
            }
        };

        Client = new CosmosClient(ConnectionString, clientOptions);

        // Retry database/container creation since the emulator can be slow to initialize
        var maxRetries = 10;
        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                var databaseResponse = await Client.CreateDatabaseIfNotExistsAsync(DatabaseName);
                var containerProperties =
                    new ContainerProperties(DocumentTypes.ContainerName, DocumentTypes.PartitionKeyPath);
                var containerResponse = await databaseResponse
                    .Database.CreateContainerIfNotExistsAsync(containerProperties);
                Container = containerResponse.Container;
                return;
            }
            catch (Exception ex) when (
                (ex is CosmosException cosmosEx &&
                    (cosmosEx.StatusCode == HttpStatusCode.ServiceUnavailable ||
                    cosmosEx.StatusCode == HttpStatusCode.InternalServerError))
                || ex is HttpRequestException)
            {
                if (attempt == maxRetries) throw;
                await Task.Delay(TimeSpan.FromSeconds(3));
            }
        }
    }

    public async ValueTask DisposeAsync()
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

public class FixRequestLocationHandler(int portNumber, HttpMessageHandler innerHandler) 
    : DelegatingHandler(innerHandler)
{
    // Workaround for dynamic port used instead of the default one.
    // See https://stackoverflow.com/a/78729014
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        const int defaultPort = 8081;
        if (request.RequestUri?.Port != defaultPort)
            return await base.SendAsync(request, cancellationToken);

        var builder = new UriBuilder(request.RequestUri)
        {
            Port = portNumber
        };
        request.RequestUri = builder.Uri;
        return await base.SendAsync(request, cancellationToken);
    }
}