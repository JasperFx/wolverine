using Bobcat.Engine;
using Shouldly;

namespace Bobcat.CritterStack.ScenarioTests;

/// <summary>
/// bobcat#177 dogfood follow-on: a constructor parameter with a C# default does not need a table
/// column. Stoat's <c>ClaimNode(string NodeClaimId, string Plan, string NodeId, string Agent,
/// TimeSpan Lease, string? Session = null)</c> is the motivating shape — before this, every
/// <c>When ClaimNode is received</c> table had to carry a Session column saying nothing.
/// </summary>
public class RecordBuildingTests
{
    private record Claim(string Plan, string Node, string Agent, TimeSpan Lease, string? Session = null);
    private record Sized(string Name, int Count = 3, TimeSpan Window = default);

    [Fact]
    public void a_defaulted_parameter_needs_no_column()
    {
        var built = (Claim)RecordBuilding.Build(typeof(Claim), new Dictionary<string, string>
        {
            ["Plan"] = "dogfood", ["Node"] = "spec-suite", ["Agent"] = "claude", ["Lease"] = "00:30:00",
        });

        built.ShouldBe(new Claim("dogfood", "spec-suite", "claude", TimeSpan.FromMinutes(30)));
        built.Session.ShouldBeNull();
    }

    [Fact]
    public void a_supplied_column_still_beats_the_default()
    {
        var built = (Claim)RecordBuilding.Build(typeof(Claim), new Dictionary<string, string>
        {
            ["Plan"] = "dogfood", ["Node"] = "spec-suite", ["Agent"] = "claude",
            ["Lease"] = "00:30:00", ["Session"] = "abc-123",
        });

        built.Session.ShouldBe("abc-123");
    }

    [Fact]
    public void a_value_type_defaulted_to_default_materializes_the_actual_default()
    {
        // `TimeSpan Window = default` reports a null DefaultValue through reflection; the builder
        // must hand the constructor default(TimeSpan), not null.
        var built = (Sized)RecordBuilding.Build(typeof(Sized), new Dictionary<string, string>
        {
            ["Name"] = "n",
        });

        built.Count.ShouldBe(3);
        built.Window.ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public void a_missing_non_defaulted_column_is_defaulted_now_that_an_act_is_partial()
    {
        // Issue #233 refused this by name; bobcat#419 made the act partial, as #241 preferred. The
        // refusal that remains is for a column matching nothing — the typo worth catching.
        var claim = (Claim)RecordBuilding.Build(typeof(Claim), new Dictionary<string, string> { ["Plan"] = "p" });

        claim.Plan.ShouldBe("p");
    }

    [Fact]
    public void the_refusal_names_the_step_when_a_step_asked()
    {
        // What the reader used to get instead was `NullReferenceException at
        // CritterStackFixture.WhenCommandIsReceived`, which names Bobcat's stack, not their spec.
        var ex = Should.Throw<SpecCriticalException>(() =>
            RecordBuilding.Build(typeof(Claim), new Dictionary<string, string> { ["Plann"] = "p" }, "When Claim is received"));

        ex.Message.ShouldStartWith("'When Claim is received': the column [Plann] matches nothing on 'Claim'");
    }

    [Fact]
    public void a_field_less_record_needs_no_columns_at_all()
    {
        // Issue #233's second question, answered yes: `new Beat()` is a perfectly good act, and an
        // emlang import carries no field information, so scaffolded scenarios routinely have
        // nothing to put in a table.
        RecordBuilding.Build(typeof(Beat), new Dictionary<string, string>()).ShouldBe(new Beat());
    }
}

public record Beat();
