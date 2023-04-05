using Shouldly;
using WolverineWebApi.Marten;

namespace Wolverine.Http.Tests.Marten;

public class using_marten_command_workflow : IntegrationContext
{
    public using_marten_command_workflow(AppFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task use_marten_command_workflow()
    {
        var result1 = await Scenario(x =>
        {
            x.Post.Json(new StartOrder(new[] { "Socks", "Shoes", "Shirt" })).ToUrl("/orders/create");
        });

        var status1 = result1.ReadAsJson<OrderStatus>();

        await Scenario(x =>
        {
            x.Post.Json(new MarkItemReady(status1.OrderId, "Socks", 1)).ToUrl("/orders/itemready");
        });

        using var session = Store.LightweightSession();
        var order = await session.Events.AggregateStreamAsync<Order>(status1.OrderId);

        order.ShouldNotBeNull();
        order.Items["Socks"].Ready.ShouldBeTrue();
    }
}