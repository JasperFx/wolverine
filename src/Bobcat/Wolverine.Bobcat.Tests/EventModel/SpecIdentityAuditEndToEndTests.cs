using Bobcat;
using JasperFx.Descriptors;
using JasperFx.Events.EventModeling;

namespace Wolverine.Bobcat.Tests.EventModel;

/// <summary>
/// Issue #338, end to end, from the projected lane: the identities this assembly's real generated
/// descriptor declares, audited against a design model.
/// </summary>
/// <remarks>
/// The pure comparison is covered in <see cref="SpecIdentityAuditTests"/>. What only a compiled spec assembly can
/// prove is that the join sees what the generator emitted, through the internal generated type.
/// <see cref="SliceIdentitySpecs"/> exists for this test and nothing else, and it declares the same
/// identities <c>SliceIdentity.feature</c> did, so the two lanes are held to one spelling.
/// </remarks>
public class SpecIdentityAuditEndToEndTests
{
    private const string Credited = "Slice Identity/A credited wallet shows the new balance";
    private const string Refused = "Slice Identity/Crediting nothing is refused";
    private const string Unspecified = "Slice Identity/The balance view is not specified yet";


    private static IReadOnlyList<SpecIdentityClaim> compiled()
    {
        var descriptor = GeneratedEventModel.For(typeof(SpecIdentityAuditEndToEndTests).Assembly);
        descriptor.ShouldNotBeNull();
        return SpecIdentityAudit.ClaimsIn(descriptor);
    }

    /// <summary>Only the feature this test owns.</summary>
    private static IReadOnlyList<SpecIdentityClaim> compiledHere()
        => compiled().Where(x => x.Identity.StartsWith("Slice Identity/")).ToList();

    /// <summary>A design model declaring exactly the identities named, on the slices named.</summary>
    private static EventModelDescriptor design(params (string Slice, string Identity)[] scenarios)
        => new("Acceptance", scenarios
            .GroupBy(x => x.Slice)
            .Select(group => new EventModelSliceDescriptor(
                group.Key, null, null, null, null,
                [], [], [])
            {
                Specifications = group.Select(x => new SpecificationDescriptor(x.Identity)).ToList(),
                Hotspots = []
            })
            .ToList());

    [Fact]
    public void the_generated_descriptor_is_reachable_out_of_a_compiled_spec_assembly()
    {
        var claims = compiled();

        // The two specified scenarios bind to the slice their tag names…
        claims.ShouldContain(x => x.Identity == Credited && x.Slice == "CreditWallet" && !x.Pending);
        claims.ShouldContain(x => x.Identity == Refused && x.Slice == "CreditWallet" && !x.Pending);

        // …and the step-less one is a claim too, marked pending. Leaving it out would report the
        // scenario as a hole — "nothing verifies this" — when the test is right there, empty.
        claims.ShouldContain(x => x.Identity == Unspecified && x.Slice == "WalletBalance" && x.Pending);
    }

    [Fact]
    public void a_model_that_declares_every_identity_has_no_drift()
    {
        var audit = SpecIdentityAudit.Compare(
            SpecIdentityAudit.ClaimsIn(design(
                ("CreditWallet", Credited),
                ("CreditWallet", Refused),
                ("WalletBalance", Unspecified))),
            compiledHere());

        audit.Drifted.ShouldBeFalse();
        audit.Matched.ShouldBe(3);
        audit.Pending.ShouldHaveSingleItem().Identity.ShouldBe(Unspecified);
        audit.Report().ShouldContain("3 matched, no drift");
    }

    [Fact]
    public void a_scenario_nobody_designed_is_an_orphan()
    {
        // The drift that survived four commits: a spec added, the model not updated.
        var audit = SpecIdentityAudit.Compare(
            SpecIdentityAudit.ClaimsIn(design(("CreditWallet", Credited), ("WalletBalance", Unspecified))),
            compiledHere());

        audit.Drifted.ShouldBeTrue();
        audit.Orphans.ShouldHaveSingleItem().Identity.ShouldBe(Refused);
        audit.Holes.ShouldBeEmpty();
        audit.Report().ShouldContain("Orphans");
        audit.Report().ShouldContain(Refused);
    }

    [Fact]
    public void a_designed_scenario_nothing_covers_is_a_hole()
    {
        var audit = SpecIdentityAudit.Compare(
            SpecIdentityAudit.ClaimsIn(design(
                ("CreditWallet", Credited),
                ("CreditWallet", Refused),
                ("WalletBalance", Unspecified),
                ("CreditWallet", "Slice Identity/Crediting a frozen wallet is refused"))),
            compiledHere());

        audit.Drifted.ShouldBeTrue();
        audit.Holes.ShouldHaveSingleItem().Identity.ShouldBe("Slice Identity/Crediting a frozen wallet is refused");
        audit.Orphans.ShouldBeEmpty();
    }

    [Fact]
    public void the_assembly_form_is_what_a_spec_project_calls()
    {
        // One line in the spec assembly's own test suite, which is the only place both facts are
        // already loaded. `excused` names what is deliberately unbound, out loud.
        // Bobcat's acceptance suite had no other slice-bound spec; this assembly does (the Wolverine vocabulary's own
        // projected specs), so the design declares those too, exactly as compiled, and only Slice Identity is in question.
        var others = compiled().Where(x => !x.Identity.StartsWith("Slice Identity/")).ToList();
        others.ShouldNotBeEmpty();

        var audit = SpecIdentityAudit.Compare(
            design([("CreditWallet", Credited), ("CreditWallet", Refused), ..others.Select(x => (x.Slice, x.Identity))]),
            new Dictionary<string, string>
            {
                [Unspecified] = "the balance view is not designed yet"
            },
            typeof(SpecIdentityAuditEndToEndTests).Assembly);

        audit.Drifted.ShouldBeFalse();
        audit.Excused.Select(x => x.Key).ShouldBe([Unspecified]);
        audit.Report().ShouldContain("the balance view is not designed yet");
    }
}
