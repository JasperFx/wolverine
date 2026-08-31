using System.Text.Json;
using Azure;
using Azure.Storage.Blobs;
using IntegrationTests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.ComplianceTests.Sagas;

namespace Wolverine.AzureBlobStorage.Tests;

/// <summary>
/// The shared saga compliance specs, run against a container. GH-4160.
/// </summary>
/// <remarks>
/// All four identity flavours, unlike CosmosDb's implementation of the same specs, which supports only
/// the string one because a Cosmos id is a string. A blob name is a string too, but the blob name
/// function is handed the identity as an <c>object</c>, so a Guid, an int and a long each address a
/// blob just as well.
/// </remarks>
public class AzureBlobStorageSagaHost : ISagaHost
{
    public const string Container = "wolverine-blob-sagas";

    private readonly BlobServiceClient _client = Azurite.CreateClient();

    public AzureBlobStorageSagaHost()
    {
        // The shipped compliance specs are plain [Fact]s, so there is neither an attribute to hang a skip
        // on nor an initialize() hook. This constructor runs inside the TEST CLASS constructor, which is
        // early enough for xUnit v3 to read a SkipException as a skip -- and it has to be that early:
        // several specs wrap their body in Should.ThrowAsync, which would swallow a skip thrown from
        // BuildHostAsync and report "expected IndeterminateSagaStateIdException, got SkipException".
        Assert.SkipUnless(Azurite.IsRunning, Azurite.SkipReason);

        // ISagaHost is constructed synchronously by the compliance specs, so this is the synchronous
        // client API rather than a blocking wait on the async one.
        _client.GetBlobContainerClient(Container).CreateIfNotExists();
    }

    public static string BlobNameFor(BlobNameContext ctx) => $"sagas/{ctx.EntityType.Name}/{ctx.Id}.json";

    public Task<IHost> BuildHostAsync<TSaga>()
    {
        return Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                // Only the saga under test. CosmosDb's implementation of these specs includes the whole
                // compliance assembly, which it can afford because it claims every type; this provider is
                // selective, so pulling in TodoHandler -- which returns Store<Todo> -- would fail codegen
                // with NoMatchingPersistenceProviderException before a single saga test ran.
                opts.Discovery.DisableConventionalDiscovery().IncludeType<TSaga>();

                opts.Services.AddSingleton(Azurite.CreateClient());

                opts.UseAzureBlobStoragePersistence(blobs => blobs.Saga(typeof(TSaga), x =>
                {
                    x.ContainerName = Container;
                    x.BlobNameFor = BlobNameFor;
                }));
            }).StartAsync();
    }

    public Task<T?> LoadState<T>(Guid id) where T : Saga => load<T>(id);

    public Task<T?> LoadState<T>(int id) where T : Saga => load<T>(id);

    public Task<T?> LoadState<T>(long id) where T : Saga => load<T>(id);

    public Task<T?> LoadState<T>(string id) where T : Saga => load<T>(id);

    // Read straight through the Azure client rather than through Wolverine, so a green test means the
    // blob really is in the container rather than that Wolverine can read back its own writes.
    private async Task<T?> load<T>(object id) where T : Saga
    {
        try
        {
            var response = await _client.GetBlobContainerClient(Container)
                .GetBlobClient(BlobNameFor(new BlobNameContext(typeof(T), id, null)))
                .DownloadContentAsync();

            return JsonSerializer.Deserialize<T>(response.Value.Content.ToMemory().Span,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch (RequestFailedException e) when (e.Status == 404)
        {
            return default;
        }
    }
}

public class string_identified_saga_compliance : StringIdentifiedSagaComplianceSpecs<AzureBlobStorageSagaHost>;

public class guid_identified_saga_compliance : GuidIdentifiedSagaComplianceSpecs<AzureBlobStorageSagaHost>;

public class int_identified_saga_compliance : IntIdentifiedSagaComplianceSpecs<AzureBlobStorageSagaHost>;

public class long_identified_saga_compliance : LongIdentifiedSagaComplianceSpecs<AzureBlobStorageSagaHost>;
