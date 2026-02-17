using Marten;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using WolverineWebApi.Bugs;
using WolverineWebApi.Marten;

namespace Wolverine.Http.Tests.Bugs;

public class Bug_2205_multiple_document_args(AppFixture fixture) : IntegrationContext(fixture)
{
    [Fact]
    public async Task multiple_documents_should_return_both()
    {
        var invoiceId = Guid.NewGuid();
        var receiptId = Guid.NewGuid();

        await using var session = Store.LightweightSession();
        session.Store(new Invoice { Id = invoiceId });
        session.Store(new Receipt { Id = receiptId });
        await session.SaveChangesAsync();

        var result = await Scenario(x =>
        {
            x.Get.Url($"/bug2205/documents/{invoiceId}/{receiptId}");
            x.StatusCodeShouldBe(200);
        });

        var text = await result.ReadAsTextAsync();
        text.ShouldContain(invoiceId.ToString());
        text.ShouldContain(receiptId.ToString());
    }

    [Fact]
    public async Task multiple_documents_returns_404_when_first_missing()
    {
        var receiptId = Guid.NewGuid();

        await using var session = Store.LightweightSession();
        session.Store(new Receipt { Id = receiptId });
        await session.SaveChangesAsync();

        await Scenario(x =>
        {
            x.Get.Url($"/bug2205/documents/{Guid.NewGuid()}/{receiptId}");
            x.StatusCodeShouldBe(404);
        });
    }

    [Fact]
    public async Task multiple_documents_returns_404_when_second_missing()
    {
        var invoiceId = Guid.NewGuid();

        await using var session = Store.LightweightSession();
        session.Store(new Invoice { Id = invoiceId });
        await session.SaveChangesAsync();

        await Scenario(x =>
        {
            x.Get.Url($"/bug2205/documents/{invoiceId}/{Guid.NewGuid()}");
            x.StatusCodeShouldBe(404);
        });
    }

    [Fact]
    public async Task document_and_aggregate_should_return_both()
    {
        var invoiceId = Guid.NewGuid();
        var orderId = Guid.NewGuid();

        await using var session = Store.LightweightSession();
        session.Store(new Invoice { Id = invoiceId });
        session.Events.StartStream<Order>(orderId, new OrderCreated([new Item { Name = "Widget" }]));
        await session.SaveChangesAsync();

        var result = await Scenario(x =>
        {
            x.Get.Url($"/bug2205/document-and-aggregate/{invoiceId}/{orderId}");
            x.StatusCodeShouldBe(200);
        });

        var text = await result.ReadAsTextAsync();
        text.ShouldContain(invoiceId.ToString());
        text.ShouldContain(orderId.ToString());
    }
}
