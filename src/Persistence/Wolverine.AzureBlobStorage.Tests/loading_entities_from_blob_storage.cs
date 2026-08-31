using IntegrationTests;
using Shouldly;

namespace Wolverine.AzureBlobStorage.Tests;

/// <summary>
/// A plain <c>[Entity]</c> parameter resolving out of Azure Blob Storage, with no other persistence
/// registered in the host at all — which is the point of the package.
/// </summary>
public class loading_entities_from_blob_storage : IClassFixture<AzureBlobStorageFixture>
{
    private readonly AzureBlobStorageFixture _fixture;

    public loading_entities_from_blob_storage(AzureBlobStorageFixture fixture)
    {
        _fixture = fixture;
        InvoiceHandler.Touched.Clear();
    }

    [AzuriteFact]
    public async Task loads_a_blob_written_outside_wolverine()
    {
        var id = Guid.NewGuid().ToString("N");
        await _fixture.PutAsync(new InvoiceContent(id, "one hundred euro"));

        var body = await _fixture.Host.MessageBus().InvokeAsync<string>(new ReadInvoice(id));

        body.ShouldBe("one hundred euro");
    }

    [AzuriteFact]
    public async Task a_missing_required_blob_stops_the_handler()
    {
        await _fixture.Host.MessageBus().InvokeAsync(new TouchInvoice(Guid.NewGuid().ToString("N")));

        InvoiceHandler.Touched.ShouldBeEmpty();
    }

    [AzuriteFact]
    public async Task a_present_required_blob_lets_the_handler_run()
    {
        var id = Guid.NewGuid().ToString("N");
        await _fixture.PutAsync(new InvoiceContent(id, "it is here"));

        await _fixture.Host.MessageBus().InvokeAsync(new TouchInvoice(id));

        InvoiceHandler.Touched.ShouldBe(["it is here"]);
    }

    [AzuriteFact]
    public async Task an_optional_blob_arrives_as_null_when_it_is_not_there()
    {
        var body = await _fixture.Host.MessageBus()
            .InvokeAsync<string>(new ReadOptionalInvoice(Guid.NewGuid().ToString("N")));

        body.ShouldBeNull();
    }

    [AzuriteFact]
    public async Task the_blob_name_function_sees_the_tenant()
    {
        var id = Guid.NewGuid().ToString("N");

        // Same id, different bodies, addressed only by the tenant segment of the blob name.
        await _fixture.PutAsync(new InvoiceContent(id, "for aap"), "aap");
        await _fixture.PutAsync(new InvoiceContent(id, "for noot"), "noot");

        var bus = _fixture.Host.MessageBus();

        (await bus.InvokeForTenantAsync<string>("aap", new ReadInvoice(id))).ShouldBe("for aap");
        (await bus.InvokeForTenantAsync<string>("noot", new ReadInvoice(id))).ShouldBe("for noot");
    }

    [AzuriteFact]
    public async Task a_tenanted_blob_is_not_visible_without_the_tenant()
    {
        var id = Guid.NewGuid().ToString("N");
        await _fixture.PutAsync(new InvoiceContent(id, "for aap"), "aap");

        var body = await _fixture.Host.MessageBus().InvokeAsync<string>(new ReadOptionalInvoice(id));

        body.ShouldBeNull();
    }
}
