using Amazon.S3;
using IntegrationTests;
using Shouldly;

namespace Wolverine.AmazonS3.Tests;

/// <summary>
/// GH-4160. Two ways this package could look like it was working while it was not, both closed
/// deliberately.
/// </summary>
public class s3_refuses_what_it_cannot_actually_do : IClassFixture<AmazonS3Fixture>
{
    private readonly AmazonS3Fixture _fixture;

    public s3_refuses_what_it_cannot_actually_do(AmazonS3Fixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// A saga chain picks its persistence provider on <c>CanApply</c>, not <c>CanPersist</c>, and this
    /// provider claims no chains. The fallback in <c>GenerationRulesExtensions</c> is the IN-MEMORY
    /// saga persistor, so a saga registered here would start cleanly, ignore its bucket and key
    /// function entirely, and keep its state in process memory. Refusing at the registration is the
    /// difference between an error and silent data loss on the next restart.
    /// </summary>
    [Fact]
    public void refuses_to_register_a_saga_type()
    {
        var configuration = new AmazonS3Configuration();

        var ex = Should.Throw<InvalidS3DocumentMappingException>(() => configuration.Store<AnS3Saga>(x =>
        {
            x.BucketName = "does-not-matter";
            x.KeyFor = ctx => $"sagas/{ctx.Id}.json";
        }));

        ex.Message.ShouldContain("not supported yet");
        ex.Message.ShouldContain("in-memory saga persistor");
    }

    /// <summary>
    /// GetObject answers 404 for a missing key AND for a missing bucket. Swallowing both would turn a
    /// mistyped bucket name into a document that is permanently, quietly absent.
    /// </summary>
    [LocalStackFact]
    public async Task a_missing_bucket_is_an_error_rather_than_a_missing_document()
    {
        var configuration = new AmazonS3Configuration();
        configuration.Store<InvoiceContent>(x =>
        {
            x.BucketName = "wolverine-s3-no-such-bucket-" + Guid.NewGuid().ToString("N");
            x.KeyFor = ctx => $"invoices/{ctx.Id}.json";
        });

        using var client = LocalStack.CreateClient();
        var session = new Internals.S3DocumentSession(client, configuration);

        var ex = await Should.ThrowAsync<AmazonS3Exception>(async () =>
            await session.LoadAsync<InvoiceContent>("some-id", null, TestContext.Current.CancellationToken));

        ex.ErrorCode.ShouldBe("NoSuchBucket");
    }

    /// <summary>
    /// ...while a missing key in a bucket that does exist stays null, which is what makes
    /// <c>[Entity(Required = false)]</c> work.
    /// </summary>
    [LocalStackFact]
    public async Task a_missing_key_in_a_real_bucket_is_still_null()
    {
        var configuration = new AmazonS3Configuration();
        configuration.Store<InvoiceContent>(x =>
        {
            // The fixture owns this bucket and has already created it
            x.BucketName = InvoiceKeys.Bucket;
            x.KeyFor = ctx => $"invoices/nothing-here/{ctx.Id}.json";
        });

        using var client = LocalStack.CreateClient();
        var session = new Internals.S3DocumentSession(client, configuration);

        (await session.LoadAsync<InvoiceContent>(Guid.NewGuid().ToString("N"), null,
            TestContext.Current.CancellationToken)).ShouldBeNull();
    }
}

public class AnS3Saga : Saga
{
    public string Id { get; set; } = null!;
}
