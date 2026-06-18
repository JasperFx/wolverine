using Alba;
using Marten;
using Shouldly;
using WolverineWebApi.Marten;

namespace Wolverine.Http.Tests.Marten;

// GH-3135: the route id flows through an [AsParameters] object while [WriteAggregate]
// IEventStream<Order> resolves the stream from that same id. uniquelau reported this combination
// returns a runtime 500 (the bare [AsParameters] FromRoute+FromBody binding works in isolation).
public class write_aggregate_with_asparameters(AppFixture fixture) : IntegrationContext(fixture)
{
    [Fact]
    public async Task write_aggregate_with_id_from_asparameters_route()
    {
        var id = Guid.NewGuid();

        await Scenario(x =>
        {
            x.Post.Json(new StartOrderWithId(id, ["Socks", "Shoes", "Shirt"])).ToUrl("/orders/create4");
        });

        var result = await Scenario(x =>
        {
            x.Post.Json(new ShipOrderPayload("FedEx")).ToUrl($"/orders/asparameters/{id}/ship");
            x.StatusCodeShouldBe(200);
        });

        var response = await result.ReadAsJsonAsync<OrderShipmentResponse>();
        response.OrderId.ShouldBe(id);
        response.Carrier.ShouldBe("FedEx");

        // The OrderShipped event was appended to the resolved stream
        using var session = Store.LightweightSession();
        var order = await session.Events.AggregateStreamAsync<Order>(id);
        order!.IsShipped().ShouldBeTrue();
    }
}
