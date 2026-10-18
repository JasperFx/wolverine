using System.Reflection;
using Bobcat;
using Bobcat.Engine;
using Bobcat.CritterStack;
using Shouldly;

namespace Bobcat.CritterStack.ScenarioTests;

/// <summary>
/// Issue #272: <see cref="RecordBuilding"/> is public API, because <c>[IncludeGrammars]</c> is the
/// designed extension point and any custom grammar taking a <see cref="StepTable"/> needs exactly
/// this conversion.
/// </summary>
/// <remarks>
/// The accessibility is asserted by reflection rather than by the tests merely compiling, because
/// this project has <c>InternalsVisibleTo</c> — it would compile against an <c>internal</c> class
/// just as happily, which is how the surface could silently close again. The consumer that matters
/// is outside the Bobcat assemblies and has no such grant.
/// </remarks>
public class RecordBuildingIsPublicApiTests
{
    // The shape a grammar module written outside Bobcat actually has: a step text, a trailing
    // table, and a runtime Type it must turn into an object. Named Parcel rather than Shipment so
    // it cannot collide by simple name with the document-lane sample (issue #270) — a {document}
    // capture resolves by simple name across the whole compilation, and two of them is BOBCAT012.
    public record Parcel(string Origin, string Destination, decimal WeightKg, string? Carrier = null);

    [Fact]
    public void RecordBuilding_is_reachable_from_outside_the_assembly()
    {
        typeof(RecordBuilding).IsPublic.ShouldBeTrue(
            "a custom [IncludeGrammars] module lives outside Bobcat and has no InternalsVisibleTo grant");
    }

    [Fact]
    public void Build_and_BuildAll_are_both_part_of_that_surface()
    {
        var members = typeof(RecordBuilding)
            .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Select(m => m.Name)
            .ToList();

        members.ShouldContain(nameof(RecordBuilding.Build));
        members.ShouldContain(nameof(RecordBuilding.BuildAll));
    }

    [Fact]
    public void a_custom_grammar_can_build_a_row_the_way_the_shipped_ones_do()
    {
        var table = new StepTable(
            ["Origin", "Destination", "WeightKg"],
            [["Dallas", "Austin", "12.5"]]);

        var built = (Parcel)RecordBuilding.BuildAll(typeof(Parcel), table).Single();

        built.Origin.ShouldBe("Dallas");
        built.Destination.ShouldBe("Austin");
        built.WeightKg.ShouldBe(12.5m);
        // A trailing optional needs no column — the same rule the shipped grammars get.
        built.Carrier.ShouldBeNull();
    }

    [Fact]
    public void the_conventions_a_custom_grammar_inherits_include_the_unmatched_column_message()
    {
        var table = new StepTable(
            ["Origin", "Destination", "WeightKg", "Wieght"],
            [["Dallas", "Austin", "12.5", "12.5"]]);

        var ex = Should.Throw<SpecCriticalException>(() =>
            RecordBuilding.BuildAll(typeof(Parcel), table, "Given shipments exist"));

        // The point of sharing the helper: one typo message, not one per consumer's copy.
        ex.Message.ShouldContain("Wieght");
        ex.Message.ShouldContain("Given shipments exist");
    }

    [Fact]
    public void BuildAll_carries_the_partial_arrange_rule_through_to_every_row()
    {
        var table = new StepTable(
            ["Origin"],
            [["Dallas"], ["Austin"]]);

        // Every build is partial since bobcat#419; the flag is kept for compatibility and changes nothing.
        RecordBuilding.BuildAll(typeof(Parcel), table).Count.ShouldBe(2);

        var arranged = RecordBuilding.BuildAll(typeof(Parcel), table, partial: true)
            .Cast<Parcel>()
            .ToList();

        arranged.Select(s => s.Origin).ShouldBe(["Dallas", "Austin"]);
        // A non-nullable string nobody specified is "" since bobcat#421, never a surprise null
        arranged.ShouldAllBe(s => s.Destination == "");
        arranged.ShouldAllBe(s => s.Carrier == null);
    }
}
