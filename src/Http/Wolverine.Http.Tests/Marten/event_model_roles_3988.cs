using JasperFx.Events.EventModeling;
using Shouldly;
using Wolverine.Configuration.Capabilities;
using Wolverine.Http.Diagnostics;
using Wolverine.Tracking;
using WolverineWebApi.Marten;

namespace Wolverine.Http.Tests.Marten;

// GH-3988: an HTTP chain derives the same Event Modeling roles a message handler chain does — with the
// route + verb as its trigger — and reaches CritterWatch through the capabilities snapshot and the
// registered IEventModelDefinitionSource. No source generator anywhere in this test project.
public class event_model_roles_3988(AppFixture fixture) : IntegrationContext(fixture)
{
    [Fact]
    public void an_http_aggregate_endpoint_is_a_command_slice_triggered_by_the_route()
    {
        // POST /orders/ship3 is Ship3(ShipOrder command, [WriteAggregate] Order order) => OrderShipped, [EmptyResponse]
        var chain = HttpChains.ChainFor("POST", "/orders/ship3");
        chain.ShouldNotBeNull();

        var slice = HttpEventModelSource.ForChain(chain);

        slice.Name.ShouldBe(nameof(ShipOrder));
        slice.Pattern.ShouldBe(SlicePattern.Command);
        slice.TriggerKind.ShouldBe(TriggerKind.Http);
        slice.TriggerOrigin!.HttpMethod.ShouldBe("POST");
        slice.TriggerOrigin.HttpRoute.ShouldBe("/orders/ship3");

        // GH-4181: the route is named by the ORIGIN, which carries it losslessly. The TriggerLabel role
        // is left unclaimed so a declared label ("Customer at the ATM") can win it -- see
        // event_model_trigger_label_4181
        slice.TriggerLabel.ShouldBeNull();
        slice.TriggerOrigin.Label.ShouldBe("POST /orders/ship3");
        slice.CommandType!.Name.ShouldBe(nameof(ShipOrder));
        slice.HandlerType!.Name.ShouldBe(nameof(MarkItemEndpoint));
        slice.AggregateTypes.Select(x => x.Name).ShouldBe(new[] { nameof(Order) });
        slice.EmittedEvents.Select(x => x.Name).ShouldBe(new[] { nameof(OrderShipped) });
        slice.PublishedMessages.ShouldBeEmpty();
    }

    [Fact]
    public void a_get_endpoint_reading_an_aggregate_is_a_view_slice()
    {
        // GET /orders/latest/{id} is GetLatest(Guid id, [ReadAggregate] Order order) => order
        var chain = HttpChains.ChainFor("GET", "/orders/latest/{id}");
        chain.ShouldNotBeNull();

        var slice = HttpEventModelSource.ForChain(chain);

        slice.Pattern.ShouldBe(SlicePattern.View);
        slice.TriggerKind.ShouldBe(TriggerKind.Http);
        slice.Name.ShouldBe("GET /orders/latest/{id}");
        slice.CommandType.ShouldBeNull();
        slice.ReadModelTypes.Select(x => x.Name).ShouldBe(new[] { nameof(Order) });
        slice.EmittedEvents.ShouldBeEmpty();
        slice.PublishedMessages.ShouldBeEmpty();
        slice.AggregateTypes.ShouldBeEmpty();
    }

    [Fact]
    public async Task the_http_slices_reach_discovery_and_the_capabilities_snapshot()
    {
        var assembled = await EventModelDiscovery.AssembleAsync(Host.Services, TestContext.Current.CancellationToken);
        var viaSeam = assembled.SelectMany(x => x.Slices).Single(x => x.Name == nameof(ShipOrder));
        viaSeam.AggregateTypes.Select(x => x.Name).ShouldBe(new[] { nameof(Order) });
        viaSeam.EmittedEvents.Select(x => x.Name).ShouldContain(nameof(OrderShipped));

        var capabilities = await ServiceCapabilities.ReadFrom(Host.GetRuntime(), null, CancellationToken.None);
        capabilities.EventModel.ShouldNotBeNull();
        var fromCapabilities = capabilities.EventModel.Slices.Single(x => x.Name == nameof(ShipOrder));
        fromCapabilities.AggregateTypes.Select(x => x.Name).ShouldBe(new[] { nameof(Order) });
        fromCapabilities.EmittedEvents.Select(x => x.Name).ShouldContain(nameof(OrderShipped));

        // the model carries the aggregate element, with the events Order applies
        var order = capabilities.EventModel.Aggregates.Single(x => x.Type.Name == nameof(Order));
        order.AppliedEvents.Select(x => x.Name).ShouldContain(nameof(OrderShipped));
    }
}
