using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using IntegrationTests;
using Shouldly;

namespace Wolverine.AzureBlobStorage.Tests;

/// <summary>
/// GH-4160. Saga storage here rests entirely on Blob Storage's conditional writes, so this pins the
/// emulator behaviour the design was built on — and the exact status codes, which are the part that
/// differs from S3.
/// </summary>
/// <remarks>
/// <para>
/// Written and run <em>before</em> any of the saga code, rather than assumed. An untestable concurrency
/// guarantee is not a guarantee; if a future Azurite stops honouring one of these, this fails with a
/// name that says what broke, instead of <c>saga_optimistic_concurrency</c> failing with a message
/// about sagas.
/// </para>
/// <para>
/// Note the asymmetry that <see cref="Internals.BlobDocumentSession" /> has to translate: a failed
/// <c>If-None-Match: *</c> is <b>409 BlobAlreadyExists</b>, while a failed <c>If-Match</c> is
/// <b>412 ConditionNotMet</b>. S3 answers 412 to both, so a port of its single-status check would let
/// every duplicate saga start through.
/// </para>
/// </remarks>
public class azurite_honors_conditional_writes : IAsyncLifetime
{
    private readonly string _containerName = "conditional-writes-" + Guid.NewGuid().ToString("N");
    private BlobContainerClient _container = null!;

    public async ValueTask InitializeAsync()
    {
        Assert.SkipUnless(Azurite.IsRunning, Azurite.SkipReason);

        _container = Azurite.ContainerClient(_containerName);
        await _container.CreateIfNotExistsAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DeleteIfExistsAsync();
        }
    }

    [AzuriteFact]
    public async Task if_none_match_star_creates_a_blob_that_is_not_there()
    {
        var response = await blob().UploadAsync(content("v1"), creating());

        response.Value.ETag.ShouldNotBe(default);
    }

    [AzuriteFact]
    public async Task if_none_match_star_is_refused_over_an_existing_blob()
    {
        var target = blob();
        await target.UploadAsync(content("v1"), creating());

        var failure = await Should.ThrowAsync<RequestFailedException>(async () =>
            await target.UploadAsync(content("v2"), creating()));

        failure.Status.ShouldBe(409);
        failure.ErrorCode.ShouldBe(BlobErrorCode.BlobAlreadyExists.ToString());
    }

    [AzuriteFact]
    public async Task a_fresh_if_match_succeeds()
    {
        var target = blob();
        var created = await target.UploadAsync(content("v1"), creating());

        var updated = await target.UploadAsync(content("v2"), updating(created.Value.ETag));

        updated.Value.ETag.ShouldNotBe(created.Value.ETag);
        (await target.DownloadContentAsync()).Value.Content.ToString().ShouldBe("v2");
    }

    [AzuriteFact]
    public async Task a_stale_if_match_is_refused_and_the_winner_survives()
    {
        var target = blob();
        var created = await target.UploadAsync(content("v1"), creating());

        await target.UploadAsync(content("v2"), updating(created.Value.ETag));

        var failure = await Should.ThrowAsync<RequestFailedException>(async () =>
            await target.UploadAsync(content("v3"), updating(created.Value.ETag)));

        failure.Status.ShouldBe(412);
        failure.ErrorCode.ShouldBe(BlobErrorCode.ConditionNotMet.ToString());

        (await target.DownloadContentAsync()).Value.Content.ToString().ShouldBe("v2");
    }

    /// <summary>
    /// An If-Match against a blob that no longer exists is 412 rather than 404, which is what lets a
    /// saga completed by another message become a concurrency failure without its own special case.
    /// </summary>
    [AzuriteFact]
    public async Task an_if_match_against_a_deleted_blob_is_a_precondition_failure()
    {
        var target = blob();
        var created = await target.UploadAsync(content("v1"), creating());
        await target.DeleteAsync();

        var failure = await Should.ThrowAsync<RequestFailedException>(async () =>
            await target.UploadAsync(content("v2"), updating(created.Value.ETag)));

        failure.Status.ShouldBe(412);
    }

    /// <summary>
    /// ...and an unconditional upload still overwrites, which is what an ordinary document write does.
    /// </summary>
    [AzuriteFact]
    public async Task an_unconditional_upload_overwrites()
    {
        var target = blob();
        await target.UploadAsync(content("v1"), new BlobUploadOptions());
        await target.UploadAsync(content("v2"), new BlobUploadOptions());

        (await target.DownloadContentAsync()).Value.Content.ToString().ShouldBe("v2");
    }

    private BlobClient blob() => _container.GetBlobClient(Guid.NewGuid().ToString("N") + ".json");

    private static BinaryData content(string body) => BinaryData.FromString(body);

    private static BlobUploadOptions creating() =>
        new() { Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All } };

    private static BlobUploadOptions updating(ETag etag) =>
        new() { Conditions = new BlobRequestConditions { IfMatch = etag } };
}
