using IntegrationTests;
using Shouldly;

namespace Wolverine.AmazonS3.Tests;

/// <summary>
/// The declarative <c>Storage.Store()</c> / <c>Delete()</c> return values and <c>UnitOfWork&lt;T&gt;</c>
/// against a bucket.
/// </summary>
public class storage_actions_against_s3 : IClassFixture<AmazonS3Fixture>
{
    private readonly AmazonS3Fixture _fixture;

    public storage_actions_against_s3(AmazonS3Fixture fixture)
    {
        _fixture = fixture;
    }

    [LocalStackFact]
    public async Task store_writes_the_object()
    {
        var id = Guid.NewGuid().ToString("N");

        await _fixture.Host.MessageBus().InvokeAsync(new WriteInvoice(id, "written by a handler"));

        var stored = await _fixture.GetAsync(id);
        stored.ShouldNotBeNull();
        stored.Body.ShouldBe("written by a handler");
    }

    [LocalStackFact]
    public async Task store_overwrites_what_is_already_there()
    {
        var id = Guid.NewGuid().ToString("N");
        await _fixture.PutAsync(new InvoiceContent(id, "first"));

        await _fixture.Host.MessageBus().InvokeAsync(new WriteInvoice(id, "second"));

        (await _fixture.GetAsync(id))!.Body.ShouldBe("second");
    }

    [LocalStackFact]
    public async Task store_puts_the_object_at_the_tenant_key()
    {
        var id = Guid.NewGuid().ToString("N");

        await _fixture.Host.MessageBus().InvokeForTenantAsync("aap", new WriteInvoice(id, "tenanted"));

        (await _fixture.GetAsync(id, "aap"))!.Body.ShouldBe("tenanted");
        (await _fixture.GetAsync(id)).ShouldBeNull();
    }

    [LocalStackFact]
    public async Task delete_removes_the_object()
    {
        var id = Guid.NewGuid().ToString("N");
        await _fixture.PutAsync(new InvoiceContent(id, "about to go"));

        await _fixture.Host.MessageBus().InvokeAsync(new DeleteInvoice(id));

        (await _fixture.GetAsync(id)).ShouldBeNull();
    }

    [LocalStackFact]
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
