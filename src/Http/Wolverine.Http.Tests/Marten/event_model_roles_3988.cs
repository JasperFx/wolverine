using JasperFx.Events.EventModeling;
using Shouldly;
using Wolverine.Configuration.Capabilities;
using Wolverine.Configuration.EventModeling;
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

    // GH-4425. Wolverine registers TWO derived sources, both on the Derived rung with a hard-coded
    // subject, and neither used to stamp Origin — so when a handler-chain slice and an HTTP-chain slice
    // disagreed on a scalar role, the hotspot read "Derived claims X; Derived claims Y" and named neither
    // file. Asserted through Describe rather than ForChain, because the stamp is applied to the finished
    // model: doing it inside the shared FinishModel would label these slices as core's.
    [Fact]
    public void the_http_source_stamps_its_own_origin_on_every_slice()
    {
        var chain = HttpChains.ChainFor("GET", "/orders/latest/{id}");
        chain.ShouldNotBeNull();

        var model = HttpEventModelSource.Describe("origin-4425", new[] { chain });

        model.Slices.ShouldNotBeEmpty();
        model.Slices.ShouldAllBe(x => x.Origin == HttpEventModelSource.SourceSubject);

        // Distinct from Wolverine core's, which is the entire point — one of these names a file. The
        // trailing slash is Uri's normalisation of an authority-based URI, not something this adds.
        HttpEventModelSource.SourceSubject.ShouldBe(new Uri("event-model://wolverine-http"));
        HttpEventModelSource.SourceSubject.ToString().ShouldBe("event-model://wolverine-http/");
        HttpEventModelSource.SourceSubject.ShouldNotBe(WolverineEventModelSource.SourceSubject);
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
        // GH-4424: the capabilities document carries the set of models; this host hosts exactly one.
        var hostedModel = capabilities.EventModel.ShouldNotBeNull().Sole.ShouldNotBeNull();
        var fromCapabilities = hostedModel.Slices.Single(x => x.Name == nameof(ShipOrder));
        fromCapabilities.AggregateTypes.Select(x => x.Name).ShouldBe(new[] { nameof(Order) });
        fromCapabilities.EmittedEvents.Select(x => x.Name).ShouldContain(nameof(OrderShipped));

        // the model carries the aggregate element, with the events Order applies
        var order = hostedModel.Aggregates.Single(x => x.Type.Name == nameof(Order));
        order.AppliedEvents.Select(x => x.Name).ShouldContain(nameof(OrderShipped));
    }
}
