using System.Security.Claims;
using Alba;
using Marten;
using Shouldly;
using WolverineWebApi.Marten;

namespace Wolverine.Http.Tests.Marten;

public class write_aggregate_from_method(AppFixture fixture) : IntegrationContext(fixture)
{
    private static ClaimsPrincipal UserWithClaims(params Claim[] claims)
    {
        var identity = new ClaimsIdentity(claims, "test");
        return new ClaimsPrincipal(identity);
    }

    [Fact]
    public async Task can_write_aggregate_using_from_method_for_id_resolution()
    {
        var orderId = Guid.NewGuid();

        // Create an order first
        await Scenario(x =>
        {
            x.Post.Json(new StartOrderWithId(orderId, ["Socks", "Shoes"])).ToUrl("/orders/create4");
        });

        // Now confirm the order using the FromMethod endpoint
        // The ResolveOrderId method reads from the "order-id" claim
        await Scenario(x =>
        {
            x.Post.Json(new ConfirmOrderFromMethod()).ToUrl("/orders/confirm-from-method");
            x.ConfigureHttpContext(c => c.User = UserWithClaims(new Claim("order-id", orderId.ToString())));
            x.StatusCodeShouldBe(204);
        });

        // Verify the order was confirmed by reading it back
        var result = await Host.GetAsJson<Order>("/orders/latest/" + orderId);
        result!.IsConfirmed.ShouldBeTrue();
    }

    [Fact]
    public async Task can_read_aggregate_using_from_method_for_id_resolution()
    {
        var orderId = Guid.NewGuid();

        // Create an order first
        await Scenario(x =>
        {
            x.Post.Json(new StartOrderWithId(orderId, ["Socks", "Shoes"])).ToUrl("/orders/create4");
        });

        // Read the order using the FromMethod endpoint
        var result = await Scenario(x =>
        {
            x.Get.Url("/orders/read-from-method");
            x.ConfigureHttpContext(c => c.User = UserWithClaims(new Claim("order-id", orderId.ToString())));
        });

        var order = result.ReadAsJson<Order>();
        order.ShouldNotBeNull();
        order!.Id.ShouldBe(orderId);
        order.Items.Keys.ShouldContain("Socks");
    }

    [Fact]
    public async Task write_aggregate_from_method_returns_404_when_aggregate_missing()
    {
        var missingId = Guid.NewGuid();

        await Scenario(x =>
        {
            x.Post.Json(new ConfirmOrderFromMethod()).ToUrl("/orders/confirm-from-method");
            x.ConfigureHttpContext(c => c.User = UserWithClaims(new Claim("order-id", missingId.ToString())));
            x.StatusCodeShouldBe(404);
        });
    }
}
