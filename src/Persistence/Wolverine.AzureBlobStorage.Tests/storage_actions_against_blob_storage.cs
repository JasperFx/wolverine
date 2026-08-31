using IntegrationTests;
using Shouldly;

namespace Wolverine.AzureBlobStorage.Tests;

/// <summary>
/// The declarative <c>Storage.Store()</c> / <c>Delete()</c> return values and <c>UnitOfWork&lt;T&gt;</c>
/// against a container.
/// </summary>
public class storage_actions_against_blob_storage : IClassFixture<AzureBlobStorageFixture>
{
    private readonly AzureBlobStorageFixture _fixture;

    public storage_actions_against_blob_storage(AzureBlobStorageFixture fixture)
    {
        _fixture = fixture;
    }

    [AzuriteFact]
    public async Task store_writes_the_blob()
    {
        var id = Guid.NewGuid().ToString("N");

        await _fixture.Host.MessageBus().InvokeAsync(new WriteInvoice(id, "written by a handler"));

        var stored = await _fixture.GetAsync(id);
        stored.ShouldNotBeNull();
        stored.Body.ShouldBe("written by a handler");
    }

    /// <summary>
    /// Blob Storage will refuse an upload over an existing blob when asked to; a DOCUMENT write asks
    /// for no conditions at all, so it must overwrite. This is the test that would catch a document
    /// write accidentally borrowing the saga's conditional path.
    /// </summary>
    [AzuriteFact]
    public async Task store_overwrites_what_is_already_there()
    {
        var id = Guid.NewGuid().ToString("N");
        await _fixture.PutAsync(new InvoiceContent(id, "first"));

        await _fixture.Host.MessageBus().InvokeAsync(new WriteInvoice(id, "second"));

        (await _fixture.GetAsync(id))!.Body.ShouldBe("second");
    }

    [AzuriteFact]
    public async Task store_puts_the_blob_at_the_tenant_name()
    {
        var id = Guid.NewGuid().ToString("N");

        await _fixture.Host.MessageBus().InvokeForTenantAsync("aap", new WriteInvoice(id, "tenanted"));

        (await _fixture.GetAsync(id, "aap"))!.Body.ShouldBe("tenanted");
        (await _fixture.GetAsync(id)).ShouldBeNull();
    }

    [AzuriteFact]
    public async Task delete_removes_the_blob()
    {
        var id = Guid.NewGuid().ToString("N");
        await _fixture.PutAsync(new InvoiceContent(id, "about to go"));

        await _fixture.Host.MessageBus().InvokeAsync(new DeleteInvoice(id));

        (await _fixture.GetAsync(id)).ShouldBeNull();
    }

    [AzuriteFact]
    public async Task a_unit_of_work_applies_every_action()
    {
        var kept = Guid.NewGuid().ToString("N");
        var removed = Guid.NewGuid().ToString("N");
        await _fixture.PutAsync(new InvoiceContent(removed, "about to go"));

        await _fixture.Host.MessageBus().InvokeAsync(new ReplaceInvoices(kept, "kept", removed));

        (await _fixture.GetAsync(kept))!.Body.ShouldBe("kept");
        (await _fixture.GetAsync(removed)).ShouldBeNull();
    }
}
