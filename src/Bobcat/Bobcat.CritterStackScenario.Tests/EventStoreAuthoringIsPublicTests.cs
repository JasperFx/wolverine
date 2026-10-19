using System.Reflection;
using Bobcat.CritterStack;
using JasperFx.Events;
using Shouldly;

namespace Bobcat.CritterStack.ScenarioTests;

/// <summary>
/// <see cref="EventStoreAuthoring"/> is part of the public store vocabulary, because a projected
/// spec has to reach it (issue #324).
/// </summary>
/// <remarks>
/// <para>
/// This test cannot fail from inside this assembly the way a consumer would experience it —
/// <c>InternalsVisibleTo</c> points here, so the type was always reachable from these tests even
/// while it was <c>internal</c>. That is precisely why the regression went unnoticed: every test
/// that used it passed. So the assertion is on the REFLECTED accessibility rather than on the call
/// compiling, which is the thing an outside consumer actually depends on.
/// </para>
/// <para>
/// The failure it guards is not hypothetical. CritterCrush's Lane A rebuild could not arrange a
/// stream from a plain xUnit test, and the compiler said <c>CS0122: 'EventStoreAuthoring' is
/// inaccessible due to its protection level</c>.
/// </para>
/// </remarks>
public class EventStoreAuthoringIsPublicTests
{
    [Fact]
    public void the_type_is_visible_outside_this_assembly()
    {
        typeof(EventStoreAuthoring).IsPublic
            .ShouldBeTrue("a projected spec outside Bobcat.CritterStack must be able to name it");
    }

    [Fact]
    public void both_operations_are_callable_and_need_nothing_but_an_event_store()
    {
        // The whole point: no IStepContext, no fixture, no run in either signature — otherwise the
        // type being public buys the projected lane nothing.
        var append = typeof(EventStoreAuthoring).GetMethod(nameof(EventStoreAuthoring.AppendAsync));
        var load = typeof(EventStoreAuthoring).GetMethod(nameof(EventStoreAuthoring.LoadDocumentAsync));

        append.ShouldNotBeNull();
        load.ShouldNotBeNull();

        // NOT MethodInfo.IsPublic — that is true for a public method on an internal type, so it
        // would pass in exactly the state this file exists to prevent. Reachability is the TYPE's
        // property, asserted above; what belongs here is the shape of the signatures.
        append!.GetParameters()[0].ParameterType.ShouldBe(typeof(IEventStore));
        load!.GetParameters()[0].ParameterType.ShouldBe(typeof(IEventStore));

        foreach (var method in new[] { append, load })
        {
            method.GetParameters()
                .ShouldAllBe(p => !p.ParameterType.FullName!.Contains("IStepContext"),
                    $"{method.Name} must stay reachable without a Bobcat run");
        }
    }

    [Fact]
    public void the_whole_store_vocabulary_tier_is_public_together()
    {
        // EventStoreAuthoring was the lone internal beside these, which is what made it read as an
        // oversight rather than a boundary. Pinned so the tier does not drift apart again.
        foreach (var type in new[]
                 {
                     typeof(EventStores), typeof(DocumentStores),
                     typeof(RecordBuilding), typeof(EventStoreAuthoring)
                 })
        {
            type.IsPublic.ShouldBeTrue($"{type.Name} is part of the public store vocabulary");
        }
    }
}
