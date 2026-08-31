using Azure;
using IntegrationTests;
using Shouldly;

namespace Wolverine.AzureBlobStorage.Tests;

/// <summary>
/// GH-4160. Two ways this package could look like it was working while it was not, both closed
/// deliberately.
/// </summary>
public class blob_storage_refuses_what_it_cannot_actually_do : IClassFixture<AzureBlobStorageFixture>
{
    private readonly AzureBlobStorageFixture _fixture;

    public blob_storage_refuses_what_it_cannot_actually_do(AzureBlobStorageFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// A saga chain picks its persistence provider on <c>CanApply</c>, not <c>CanPersist</c>, and this
    /// provider claims a chain only for a type registered through <c>Saga&lt;T&gt;()</c>. Through
    /// <c>Store&lt;T&gt;()</c> the fallback is the IN-MEMORY saga persistor, so the saga would start
    /// cleanly, ignore its container and blob name function entirely, and keep its state in process
    /// memory. Refusing at the registration is the difference between an error and silent data loss on
    /// the next restart.
    /// </summary>
    [Fact]
    public void refuses_to_register_a_saga_type_as_an_ordinary_document()
    {
        var configuration = new AzureBlobStorageConfiguration();

        var ex = Should.Throw<InvalidBlobDocumentMappingException>(() => configuration.Store<ABlobSaga>(x =>
        {
            x.ContainerName = "does-not-matter";
            x.BlobNameFor = ctx => $"sagas/{ctx.Id}.json";
        }));

        ex.Message.ShouldContain("Saga<ABlobSaga>(...)");
        ex.Message.ShouldContain("in-memory saga persistor");
    }

    /// <summary>
    /// ...and the mirror, so neither registration silently accepts the other's type.
    /// </summary>
    [Fact]
    public void refuses_to_register_a_non_saga_as_a_saga()
    {
        var configuration = new AzureBlobStorageConfiguration();

        var ex = Should.Throw<InvalidBlobDocumentMappingException>(() =>
            configuration.Saga(typeof(NotASaga), x =>
            {
                x.ContainerName = "does-not-matter";
                x.BlobNameFor = ctx => $"nope/{ctx.Id}.json";
            }));

        ex.Message.ShouldContain("does not derive from Saga");
    }

    /// <summary>
    /// A download answers 404 for a missing blob AND for a missing container. Swallowing both would turn
    /// a mistyped container name into a document that is permanently, quietly absent.
    /// </summary>
    [AzuriteFact]
    public async Task a_missing_container_is_an_error_rather_than_a_missing_document()
    {
        var configuration = new AzureBlobStorageConfiguration();
        configuration.Store<InvoiceContent>(x =>
        {
            x.ContainerName = "wolverine-no-such-container-" + Guid.NewGuid().ToString("N");
            x.BlobNameFor = ctx => $"invoices/{ctx.Id}.json";
        });

        var session = new Internals.BlobDocumentSession(Azurite.CreateClient(), configuration);

        var ex = await Should.ThrowAsync<RequestFailedException>(async () =>
            await session.LoadAsync<InvoiceContent>("some-id", null, TestContext.Current.CancellationToken));

        ex.ErrorCode.ShouldBe("ContainerNotFound");
    }

    /// <summary>
    /// ...while a missing blob in a container that does exist stays null, which is what makes
    /// <c>[Entity(Required = false)]</c> work.
    /// </summary>
    [AzuriteFact]
    public async Task a_missing_blob_in_a_real_container_is_still_null()
    {
        var configuration = new AzureBlobStorageConfiguration();
        configuration.Store<InvoiceContent>(x =>
        {
            // The fixture owns this container and has already created it
            x.ContainerName = InvoiceNames.Container;
            x.BlobNameFor = ctx => $"invoices/nothing-here/{ctx.Id}.json";
        });

        var session = new Internals.BlobDocumentSession(Azurite.CreateClient(), configuration);

        (await session.LoadAsync<InvoiceContent>(Guid.NewGuid().ToString("N"), null,
            TestContext.Current.CancellationToken)).ShouldBeNull();
    }
}

public class ABlobSaga : Saga
{
    public string Id { get; set; } = null!;
}

public class NotASaga
{
    public string Id { get; set; } = null!;
}
