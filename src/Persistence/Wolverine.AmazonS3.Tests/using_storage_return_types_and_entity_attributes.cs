using System.Text.Json;
using Amazon.S3;
using Amazon.S3.Model;
using IntegrationTests;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.ComplianceTests;

namespace Wolverine.AmazonS3.Tests;

/// <summary>
/// The shared <see cref="StorageActionCompliance" /> suite, run against a bucket. Marten, EF Core,
/// Polecat, RavenDb and the in-memory provider all answer this same suite, and answering it is what
/// makes "S3 supports the declarative storage return values" a claim with the same meaning it has for
/// every other store.
/// </summary>
/// <remarks>
/// This covers what a hand-written suite kept missing: <c>Storage.Insert()</c> and
/// <c>Storage.Update()</c> as return values (which reach <c>DetermineInsertFrame</c> and
/// <c>DetermineUpdateFrame</c> — untouched by any other test here), all four
/// <see cref="Wolverine.Persistence.StorageAction" /> arms through the generic path including
/// <c>Nothing</c>, null actions, and <c>[Entity]</c> on Before methods.
/// </remarks>
public class using_storage_return_types_and_entity_attributes : StorageActionCompliance
{
    public const string Bucket = "wolverine-s3-compliance";

    private AmazonS3Client _client = null!;

    protected override void configureWolverine(WolverineOptions opts)
    {
        opts.Durability.Mode = DurabilityMode.Solo;

        opts.Services.AddSingleton<IAmazonS3>(LocalStack.CreateClient());

        opts.UseAmazonS3Persistence(s3 =>
        {
            s3.Store<Todo>(x =>
            {
                x.BucketName = Bucket;
                x.KeyFor = ctx => $"todos/{ctx.Id}.json";
            });
        });
    }

    // The base class builds the host first and calls this after, and building the host makes no S3
    // call -- so this is the first place that needs LocalStack, and the place to skip from. The
    // compliance suite's tests are [Fact], so [LocalStackFact] is not available to them.
    protected override async Task initialize()
    {
        Assert.SkipUnless(LocalStack.IsRunning, LocalStack.SkipReason);

        _client = LocalStack.CreateClient();
        Disposables.Add(_client);

        try
        {
            await _client.PutBucketAsync(new PutBucketRequest { BucketName = Bucket });
        }
        catch (AmazonS3Exception e) when (e.ErrorCode is "BucketAlreadyOwnedByYou" or "BucketAlreadyExists")
        {
        }
    }

    public override async Task<Todo?> Load(string id)
    {
        try
        {
            using var response = await _client.GetObjectAsync(Bucket, keyFor(id));
            using var reader = new StreamReader(response.ResponseStream);

            return JsonSerializer.Deserialize<Todo>(await reader.ReadToEndAsync(), serialization);
        }
        catch (AmazonS3Exception e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public override Task Persist(Todo todo)
    {
        return _client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = Bucket,
            Key = keyFor(todo.Id),
            ContentBody = JsonSerializer.Serialize(todo, serialization)
        });
    }

    // Read and written straight through the AWS client rather than through Wolverine, so these have
    // to agree with the mapping's key function and with S3DocumentSerializer.Default by hand.
    private static string keyFor(string id) => $"todos/{id}.json";

    private static readonly JsonSerializerOptions serialization = new(JsonSerializerDefaults.Web);
}
