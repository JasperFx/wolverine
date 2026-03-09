using Marten;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using WolverineWebApi.Marten;

namespace Wolverine.Http.Tests.Marten;

public class using_version_source_override(AppFixture fixture) : IntegrationContext(fixture)
{
    private async Task<Guid> CreateOrder()
    {
        var result = await Scenario(x =>
        {
            x.Post.Json(new StartOrder(["Socks", "Shoes", "Shirt"])).ToUrl("/orders/create");
        });

        var status = result.ReadAsJson<OrderStatus>();
        return status.OrderId;
    }

    [Fact]
    public async Task happy_path_with_version_source_from_route_argument()
    {
        var orderId = await CreateOrder();

        // version 1 matches the stream after creation
        await Scenario(x =>
        {
            x.Post.Json(new ShipOrderWithExpectedVersion(orderId, 1))
                .ToUrl($"/orders/{orderId}/ship-with-expected-version/1");
            x.StatusCodeShouldBe(204);
        });

        await using var session = Store.LightweightSession();
        var order = await session.Events.AggregateStreamAsync<Order>(orderId);
        order.ShouldNotBeNull();
        order.Shipped.HasValue.ShouldBeTrue();
    }

    [Fact]
    public async Task wrong_version_from_route_argument_returns_500()
    {
        var orderId = await CreateOrder();

        // version 99 does not match - should fail
        await Scenario(x =>
        {
            x.Post.Json(new ShipOrderWithExpectedVersion(orderId, 99))
                .ToUrl($"/orders/{orderId}/ship-with-expected-version/99");
            x.StatusCodeShouldBe(500);
        });
    }

    [Fact]
    public async Task happy_path_with_version_source_from_request_body()
    {
        var orderId = await CreateOrder();

        await Scenario(x =>
        {
            x.Post.Json(new ShipOrderWithExpectedVersion(orderId, 1))
                .ToUrl("/orders/ship-with-body-version");
            x.StatusCodeShouldBe(204);
        });

        await using var session = Store.LightweightSession();
        var order = await session.Events.AggregateStreamAsync<Order>(orderId);
        order.ShouldNotBeNull();
        order.Shipped.HasValue.ShouldBeTrue();
    }

    [Fact]
    public async Task wrong_version_from_request_body_returns_500()
    {
        var orderId = await CreateOrder();

        await Scenario(x =>
        {
            x.Post.Json(new ShipOrderWithExpectedVersion(orderId, 99))
                .ToUrl("/orders/ship-with-body-version");
            x.StatusCodeShouldBe(500);
        });
    }
}
