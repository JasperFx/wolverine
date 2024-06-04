using Alba;
using Shouldly;
using WolverineWebApi.Marten;

namespace Wolverine.Http.Tests.Marten;

public class document_attribute_usage : IntegrationContext
{
    public document_attribute_usage(AppFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task returns_404_on_id_miss()
    {
        // Using Alba to run a request for a non-existent
        // Invoice document
        await Scenario(x =>
        {
            x.Get.Url("/invoices/" + Guid.NewGuid());
            x.StatusCodeShouldBe(404);
        });
    }

    [Fact]
    public async Task returns_404_when_soft_deleted()
    {
        var invoice = new Invoice();
        using var session = Store.LightweightSession();
        session.Store(invoice);
        await session.SaveChangesAsync();
        
        session.Delete(invoice);
        await session.SaveChangesAsync();

        await Scenario(x =>
        {
            x.Get.Url("/invoices/soft-delete/" + invoice.Id);
            x.StatusCodeShouldBe(404);
        });
    }

    [Fact]
    public async Task default_to_id_route()
    {
        var invoice = new Invoice();
        using var session = Store.LightweightSession();
        session.Store(invoice);
        await session.SaveChangesAsync();


        var invoice2 = await Host.GetAsJson<Invoice>("/invoices/" + invoice.Id);
        invoice2.ShouldNotBeNull();
    }

    [Fact]
    public async Task try_to_use_document_name_id_naming_convention()
    {
        var invoice = new Invoice();
        using var session = Store.LightweightSession();
        session.Store(invoice);
        await session.SaveChangesAsync();

        await Host.Scenario(x =>
        {
            x.Post.Url($"/invoices/{invoice.Id}/pay");
            x.StatusCodeShouldBe(204);
        });

        var loaded = await session.LoadAsync<Invoice>(invoice.Id);
        loaded.Paid.ShouldBeTrue();
    }

    [Fact]
    public async Task use_explicit_path_argument()
    {
        var invoice = new Invoice();
        await using var session = Store.LightweightSession();
        session.Store(invoice);
        await session.SaveChangesAsync();

        await Host.Scenario(x =>
        {
            x.Post.Url($"/invoices/{invoice.Id}/approve");
            x.StatusCodeShouldBe(204);
        });

        var loaded = await session.LoadAsync<Invoice>(invoice.Id);
        loaded.Approved.ShouldBeTrue();
    }
}