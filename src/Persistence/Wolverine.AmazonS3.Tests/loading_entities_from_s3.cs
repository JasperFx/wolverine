using IntegrationTests;
using Shouldly;

namespace Wolverine.AmazonS3.Tests;

/// <summary>
/// A plain <c>[Entity]</c> parameter resolving out of S3, with no other persistence registered in the
/// host at all — which is the point of the package.
/// </summary>
public class loading_entities_from_s3 : IClassFixture<AmazonS3Fixture>
{
    private readonly AmazonS3Fixture _fixture;

    public loading_entities_from_s3(AmazonS3Fixture fixture)
    {
        _fixture = fixture;
        InvoiceHandler.Touched.Clear();
    }

    [LocalStackFact]
    public async Task loads_an_object_written_outside_wolverine()
    {
        var id = Guid.NewGuid().ToString("N");
        await _fixture.PutAsync(new InvoiceContent(id, "one hundred euro"));

        var body = await _fixture.Host.MessageBus().InvokeAsync<string>(new ReadInvoice(id));

        body.ShouldBe("one hundred euro");
    }

    [LocalStackFact]
    public async Task a_missing_required_object_stops_the_handler()
    {
        await _fixture.Host.MessageBus().InvokeAsync(new TouchInvoice(Guid.NewGuid().ToString("N")));

        InvoiceHandler.Touched.ShouldBeEmpty();
    }

    [LocalStackFact]
    public async Task a_present_required_object_lets_the_handler_run()
    {
        var id = Guid.NewGuid().ToString("N");
        await _fixture.PutAsync(new InvoiceContent(id, "it is here"));

        await _fixture.Host.MessageBus().InvokeAsync(new TouchInvoice(id));

        InvoiceHandler.Touched.ShouldBe(["it is here"]);
    }

    [LocalStackFact]
    public async Task an_optional_object_arrives_as_null_when_it_is_not_there()
    {
        var body = await _fixture.Host.MessageBus()
            .InvokeAsync<string>(new ReadOptionalInvoice(Guid.NewGuid().ToString("N")));

        body.ShouldBeNull();
    }

    [LocalStackFact]
    public async Task the_key_function_sees_the_tenant()
    {
        var id = Guid.NewGuid().ToString("N");

        // Same id, different bodies, addressed only by the tenant segment of the key.
        await _fixture.PutAsync(new InvoiceContent(id, "for aap"), "aap");
        await _fixture.PutAsync(new InvoiceContent(id, "for noot"), "noot");

        var bus = _fixture.Host.MessageBus();

        (await bus.InvokeForTenantAsync<string>("aap", new ReadInvoice(id))).ShouldBe("for aap");
        (await bus.InvokeForTenantAsync<string>("noot", new ReadInvoice(id))).ShouldBe("for noot");
    }

    [LocalStackFact]
    public async Task a_tenanted_key_is_not_visible_without_the_tenant()
    {
        var id = Guid.NewGuid().ToString("N");
        await _fixture.PutAsync(new InvoiceContent(id, "for aap"), "aap");

        var body = await _fixture.Host.MessageBus().InvokeAsync<string>(new ReadOptionalInvoice(id));

        body.ShouldBeNull();
    }
}
