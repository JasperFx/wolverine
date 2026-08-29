using JasperFx.Descriptors;
using JasperFx.Events.EventModeling;
using Shouldly;
using Wolverine.Http.Diagnostics;
using WolverineWebApi.Marten;

namespace Wolverine.Http.Tests.Marten;

// GH-4181. An HTTP slice used to claim TriggerLabel with "{verb} {route}", which is a Derived claim on a
// role per-role precedence (jasperfx#703) then made unbeatable -- so an overlay's human trigger label
// ("Customer at the ATM") always lost, and every labelled endpoint minted a SourceDisagreement hotspot
// for the privilege. The route is already carried losslessly on TriggerOrigin, so the claim added no
// information and only occupied the one slot a declaration could use.
public class event_model_trigger_label_4181(AppFixture fixture) : IntegrationContext(fixture)
{
    private const string DeclaredLabel = "Customer at the ATM";

    private EventModelSliceDescriptor theDerivedSlice()
    {
        var chain = HttpChains.ChainFor("POST", "/orders/ship3");
        chain.ShouldNotBeNull();

        return HttpEventModelSource.ForChain(chain);
    }

    [Fact]
    public void the_derived_slice_leaves_the_trigger_label_unclaimed()
    {
        var slice = theDerivedSlice();

        slice.TriggerLabel.ShouldBeNull();

        // and nothing is lost: the verb and the route are still there, structured
        slice.TriggerKind.ShouldBe(TriggerKind.Http);
        slice.TriggerOrigin!.Label.ShouldBe("POST /orders/ship3");
        slice.TriggerOrigin.HttpMethod.ShouldBe("POST");
        slice.TriggerOrigin.HttpRoute.ShouldBe("/orders/ship3");
    }

    [Fact]
    public void the_wireframe_trigger_element_still_names_the_route()
    {
        // Elements are computed from the roles, and a trigger is the one element kind with three
        // possible sources -- TriggerType, TriggerLabel and TriggerOrigin. Withholding the label must
        // not cost the canvas its trigger, which is the whole reason the origin is safe to rely on
        var trigger = theDerivedSlice().Elements.Single(x => x.Kind == EventModelElementKind.Trigger);

        trigger.Label.ShouldBe("POST /orders/ship3");
    }

    [Fact]
    public void a_declared_label_wins_the_role_and_records_no_disagreement()
    {
        // The overlay is merged FIRST on purpose: it must win on the ladder, not on ordering
        var declared = new EventModelDescriptor("trigger-label-4181", [
            new EventModelSliceDescriptor(
                nameof(ShipOrder), DeclaredLabel, null, null, null,
                Array.Empty<TypeDescriptor>(), Array.Empty<TypeDescriptor>(), Array.Empty<TypeDescriptor>())
        ]);

        var derived = new EventModelDescriptor("trigger-label-4181", [theDerivedSlice()])
            .WithProvenance(EventModelProvenance.Derived);

        var merged = EventModelDescriptor.Merge("trigger-label-4181", [declared, derived]);
        var slice = merged.Slices.Single(x => x.Name == nameof(ShipOrder));

        slice.TriggerLabel.ShouldBe(DeclaredLabel);

        // the derived roles are untouched -- the overlay filled a gap, it did not take anything
        slice.TriggerKind.ShouldBe(TriggerKind.Http);
        slice.TriggerOrigin!.HttpRoute.ShouldBe("/orders/ship3");
        slice.AggregateTypes.Select(x => x.Name).ShouldBe([nameof(Order)]);
        slice.EmittedEvents.Select(x => x.Name).ShouldContain(nameof(OrderShipped));

        // and the label was a gap, not a disagreement: five labelled endpoints used to mean five of
        // these hotspots drowning out any real finding
        slice.Hotspots.ShouldNotContain(x =>
            x.Origin == HotspotOrigin.SourceDisagreement && x.Role == EventModelRole.TriggerLabel);
    }
}
