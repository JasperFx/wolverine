using System.Text.Json;
using Amazon.S3;
using Amazon.S3.Model;
using IntegrationTests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.ComplianceTests.Sagas;

namespace Wolverine.AmazonS3.Tests;

/// <summary>
/// The shared saga compliance specs, run against a bucket. GH-4160.
/// </summary>
/// <remarks>
/// All four identity flavours, unlike CosmosDb's implementation of the same specs, which supports only
/// the string one because a Cosmos id is a string. An S3 object key is a string too, but the key
/// function is handed the identity as an <c>object</c>, so a Guid, an int and a long each address a
/// key just as well.
/// </remarks>
public class AmazonS3SagaHost : ISagaHost
{
    public const string Bucket = "wolverine-s3-sagas";

    private readonly AmazonS3Client _client = LocalStack.CreateClient();

    public AmazonS3SagaHost()
    {
        // The skip is taken HERE rather than in BuildHostAsync, and that is not a style choice:
        // several of the shipped saga specs wrap their body in Should.ThrowAsync, which swallows the
        // SkipException and reports it as "expected IndeterminateSagaStateIdException, got
        // SkipException". From the constructor it skips cleanly.
        Assert.SkipUnless(LocalStack.IsRunning, LocalStack.SkipReason);

        try
        {
#pragma warning disable VSTHRD002 // ISagaHost is constructed synchronously by the compliance specs
            _client.PutBucketAsync(new PutBucketRequest { BucketName = Bucket }).GetAwaiter().GetResult();
#pragma warning restore VSTHRD002
        }
        catch (AmazonS3Exception e) when (e.ErrorCode is "BucketAlreadyOwnedByYou" or "BucketAlreadyExists")
        {
        }
    }

    public static string KeyFor(S3KeyContext ctx) => $"sagas/{ctx.EntityType.Name}/{ctx.Id}.json";

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

                opts.Services.AddSingleton<IAmazonS3>(LocalStack.CreateClient());

                opts.UseAmazonS3Persistence(s3 => s3.Saga(typeof(TSaga), x =>
                {
                    x.BucketName = Bucket;
                    x.KeyFor = KeyFor;
                }));
            }).StartAsync();
    }

    public Task<T?> LoadState<T>(Guid id) where T : Saga => load<T>(id);

    public Task<T?> LoadState<T>(int id) where T : Saga => load<T>(id);

    public Task<T?> LoadState<T>(long id) where T : Saga => load<T>(id);

    public Task<T?> LoadState<T>(string id) where T : Saga => load<T>(id);

    // Read straight through the AWS client rather than through Wolverine, so a green test means the
    // object really is in the bucket rather than that Wolverine can read back its own writes.
    private async Task<T?> load<T>(object id) where T : Saga
    {
        try
        {
            using var response = await _client.GetObjectAsync(Bucket,
                KeyFor(new S3KeyContext(typeof(T), id, null)));
            using var reader = new StreamReader(response.ResponseStream);

            return JsonSerializer.Deserialize<T>(await reader.ReadToEndAsync(),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch (AmazonS3Exception e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return default;
        }
    }
}

public class string_identified_saga_compliance : StringIdentifiedSagaComplianceSpecs<AmazonS3SagaHost>;

public class guid_identified_saga_compliance : GuidIdentifiedSagaComplianceSpecs<AmazonS3SagaHost>;

public class int_identified_saga_compliance : IntIdentifiedSagaComplianceSpecs<AmazonS3SagaHost>;

public class long_identified_saga_compliance : LongIdentifiedSagaComplianceSpecs<AmazonS3SagaHost>;
