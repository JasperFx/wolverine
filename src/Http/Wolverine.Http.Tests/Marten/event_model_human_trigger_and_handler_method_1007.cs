using JasperFx.Events.EventModeling;
using Shouldly;
using Wolverine.Http.Diagnostics;
using WolverineWebApi.Marten;

namespace Wolverine.Http.Tests.Marten;

// jasperfx#1007. An HTTP slice names the user's endpoint class and method -- never the generated HttpHandler
// subclass and its Handle override -- and a board's Human trigger merges with the route without a
// SourceDisagreement: the route says HOW the command arrives, the board says WHO sends it.
public class event_model_human_trigger_and_handler_method_1007(AppFixture fixture) : IntegrationContext(fixture)
{
    private EventModelSliceDescriptor theDerivedSlice()
    {
        var chain = HttpChains.ChainFor("POST", "/orders/ship3");
        chain.ShouldNotBeNull();

        return HttpEventModelSource.ForChain(chain);
    }

    [Fact]
    public void the_slice_names_the_users_endpoint_class_and_method()
    {
        var chain = HttpChains.ChainFor("POST", "/orders/ship3")!;
        var slice = theDerivedSlice();

        slice.HandlerType!.FullName.ShouldBe(typeof(MarkItemEndpoint).FullName);
        slice.HandlerMethod.ShouldBe(nameof(MarkItemEndpoint.Ship3));

        // never the generated HttpHandler, whose type is named for the chain's file name
        slice.HandlerType.Name.ShouldNotBe(chain.ToString());
        typeof(MarkItemEndpoint).IsSubclassOf(typeof(HttpHandler)).ShouldBeFalse();
        slice.HandlerMethod.ShouldNotBe("Handle");
    }

    [Fact]
    public void a_declared_human_trigger_merges_with_the_route_without_a_disagreement()
    {
        var builder = new EventModelBuilder();
        builder.Slice(nameof(ShipOrder)).TriggeredBy("Ship it", TriggerKind.Human);
        var declared = builder.Build("human-trigger-1007").WithProvenance(EventModelProvenance.Declared);

        var derived = new EventModelDescriptor("human-trigger-1007", [theDerivedSlice()])
            .WithProvenance(EventModelProvenance.Derived);

        var slice = EventModelDescriptor.Merge("human-trigger-1007", [declared, derived])
            .Slices.Single(x => x.Name == nameof(ShipOrder));

        slice.Hotspots.ShouldNotContain(x => x.Origin == HotspotOrigin.SourceDisagreement);
        slice.TriggerKind.ShouldBe(TriggerKind.Http);
        slice.TriggeredBy.ShouldBe(TriggerKind.Human);
        slice.TriggerOrigin!.HttpRoute.ShouldBe("/orders/ship3");
        slice.TriggerLabel.ShouldBe("Ship it");
        slice.HandlerMethod.ShouldBe(nameof(MarkItemEndpoint.Ship3));
    }
}
