using Bobcat;
using System.Reflection;
using JasperFx.Events.EventModeling;

namespace Wolverine.Bobcat.Tests.EventModel;

/// <summary>
/// bobcat#338: the comparison itself. The end-to-end half — reading the identities out of a
/// compiled spec assembly's internal generated type — is <see cref="SpecIdentityAuditEndToEndTests"/>.
/// Both moved here from Bobcat with <see cref="SpecIdentityAudit"/> (GH-5082, JasperFx/bobcat#744).
/// </summary>
public class SpecIdentityAuditTests
{
    private static EventModelDescriptor model(params (string Slice, string Identity, bool Pending)[] claims)
        => new("M", claims
            .GroupBy(x => x.Slice)
            .Select(group => new EventModelSliceDescriptor(group.Key, null, null, null, null, [], [], [])
            {
                Specifications = group.Where(x => !x.Pending)
                    .Select(x => new SpecificationDescriptor(x.Identity)).ToList(),
                Hotspots = group.Where(x => x.Pending)
                    .Select(x => HotspotDescriptor.PendingSpecification(x.Identity)).ToList()
            })
            .ToList());

    private static (string, string, bool) on(string slice, string identity) => (slice, identity, false);

    [Fact]
    public void identities_that_join_are_matched_and_nothing_is_reported()
    {
        var audit = SpecIdentityAudit.Compare(
            model(on("CreditWallet", "Wallet/A wallet is credited")),
            model(on("CreditWallet", "Wallet/A wallet is credited")));

        audit.Drifted.ShouldBeFalse();
        audit.Matched.ShouldBe(1);
        audit.Report().ShouldBe("Spec identities: 1 matched, no drift." + Environment.NewLine);
    }

    [Fact]
    public void a_test_identity_the_model_does_not_declare_is_an_orphan()
    {
        // The exact drift #338 was filed for: a spec added, the model left alone. Green suite,
        // clean build, four commits.
        var audit = SpecIdentityAudit.Compare(
            model(on("CreditWallet", "Wallet/A wallet is credited")),
            model(
                on("CreditWallet", "Wallet/A wallet is credited"),
                on("CreditWallet", "Wallet/A wallet nobody designed")));

        audit.Drifted.ShouldBeTrue();
        audit.Matched.ShouldBe(1);

        var orphan = audit.Orphans.ShouldHaveSingleItem();
        orphan.Identity.ShouldBe("Wallet/A wallet nobody designed");
        orphan.Slice.ShouldBe("CreditWallet");

        audit.Report().ShouldContain("a test declares this identity and the model does not");
    }

    [Fact]
    public void a_model_scenario_no_test_covers_is_a_hole()
    {
        var audit = SpecIdentityAudit.Compare(
            model(
                on("CreditWallet", "Wallet/A wallet is credited"),
                on("CreditWallet", "Wallet/A frozen wallet is refused")),
            model(on("CreditWallet", "Wallet/A wallet is credited")));

        audit.Drifted.ShouldBeTrue();
        audit.Holes.ShouldHaveSingleItem().Identity.ShouldBe("Wallet/A frozen wallet is refused");
        audit.Orphans.ShouldBeEmpty();
        audit.Report().ShouldContain("the model declares this identity and no test covers it");
    }

    [Fact]
    public void the_two_directions_are_separate_findings()
    {
        // An orphan and a hole are not one discrepancy seen twice: one says the canvas shows a
        // specification for something nobody designed, the other says the design declares
        // behaviour nothing verifies. A count alone would call this suite even.
        var audit = SpecIdentityAudit.Compare(
            model(on("CreditWallet", "Wallet/A frozen wallet is refused")),
            model(on("CreditWallet", "Wallet/A wallet nobody designed")));

        audit.Orphans.ShouldHaveSingleItem();
        audit.Holes.ShouldHaveSingleItem();
        audit.Matched.ShouldBe(0);
    }

    [Fact]
    public void an_identity_bound_to_the_wrong_slice_is_misbound()
    {
        // Neither BOBCAT025 nor BOBCAT026 can see this: both compare slice NAMES, and both names
        // here are real. The identity joins, and points at the wrong behaviour on the canvas.
        var audit = SpecIdentityAudit.Compare(
            model(on("CreditWallet", "Wallet/A wallet is credited")),
            model(on("DebitWallet", "Wallet/A wallet is credited")));

        audit.Drifted.ShouldBeTrue();

        var misbound = audit.Misbound.ShouldHaveSingleItem();
        misbound.Identity.ShouldBe("Wallet/A wallet is credited");
        misbound.DeclaredOn.ShouldBe("CreditWallet");
        misbound.BoundTo.ShouldBe("DebitWallet");
    }

    [Fact]
    public void several_features_may_feed_one_slice_without_being_misbound()
    {
        // A slice is a behaviour, not a document: three features describing CreditWallet is the
        // shipped case (Wallet.feature tags it three times). Comparing the SET of slices per
        // identity is what keeps that from reading as drift.
        var declared = model(
            on("CreditWallet", "Wallet/A wallet is credited"),
            on("CreditWallet", "WalletHttp/A wallet is credited over HTTP"));

        var audit = SpecIdentityAudit.Compare(declared, declared);

        audit.Drifted.ShouldBeFalse();
        audit.Matched.ShouldBe(2);
    }

    [Fact]
    public void a_pending_specification_joins_and_is_not_drift()
    {
        // The identity is real and the scenario exists; it just has no steps, which is already a
        // hotspot on the canvas (jasperfx#689). Reporting it as a hole would read as a missing
        // test, and failing a build for it would make this audit an unrelated policy.
        var audit = SpecIdentityAudit.Compare(
            model(on("WalletBalance", "Wallet/The balance view")),
            model(("WalletBalance", "Wallet/The balance view", true)));

        audit.Drifted.ShouldBeFalse();
        audit.Matched.ShouldBe(1);
        audit.Pending.ShouldHaveSingleItem().Identity.ShouldBe("Wallet/The balance view");
        audit.Report().ShouldContain("the scenario has no steps");
    }

    [Fact]
    public void an_excused_identity_is_left_out_of_both_directions_and_named_in_the_report()
    {
        // The escape hatch said out loud (#338): a chapter-wide invariant test that binds no
        // slice, or a model scenario specified in a lane Bobcat cannot see. It has to carry a
        // reason, because "deliberately not a spec" is a claim someone should have to make.
        var audit = SpecIdentityAudit.Compare(
            model(on("CreditWallet", "Wallet/Specified elsewhere")),
            model(on("CreditWallet", "Wallet/Deliberately unbound")),
            new Dictionary<string, string>
            {
                ["Wallet/Specified elsewhere"] = "covered by the ledger invariant suite",
                ["Wallet/Deliberately unbound"] = "a chapter-wide invariant, bound to no slice"
            });

        audit.Drifted.ShouldBeFalse();
        audit.Orphans.ShouldBeEmpty();
        audit.Holes.ShouldBeEmpty();
        audit.Report().ShouldContain("covered by the ledger invariant suite");
    }

    // In Bobcat this was the test assembly itself, which declared no slices. This one does (SliceIdentitySpecs and the
    // vocabulary's own projected specs), so the assembly with no generated source is WolverineFx.Bobcat: it is compiled
    // without Bobcat's generator.
    private static readonly Assembly noGeneratedSource = typeof(SpecIdentityAudit).Assembly;

    [Fact]
    public void auditing_an_assembly_with_no_generated_source_refuses_rather_than_reporting_holes()
    {
        // Silence would be a confident lie: every declared scenario would come back uncovered,
        // which is the same failure as zero-filling an unmeasured duration.
        var refusal = Should.Throw<InvalidOperationException>(() => SpecIdentityAudit.Compare(
            model(on("CreditWallet", "Wallet/A wallet is credited")),
            noGeneratedSource));

        refusal.Message.ShouldContain("has a generated");
        // The Gherkin lane and its @slice: tag left in 0.50.0 (bobcat#660): name what binds a slice now.
        refusal.Message.ShouldContain("[BobcatSpec(typeof(TheCommand))]");
        refusal.Message.ShouldNotContain("@slice:");
    }

    [Fact]
    public void an_assembly_with_no_generated_event_model_reads_as_null_not_as_a_failure()
    {
        // An assembly whose specs declare no slices legitimately has no generated source. That is a
        // fact for the caller to interpret, not an error.
        GeneratedEventModel.For(noGeneratedSource).ShouldBeNull();
        GeneratedEventModel.For(typeof(Assembly).Assembly).ShouldBeNull();
    }
}
