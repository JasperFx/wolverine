using Wolverine.ErrorHandling;
using Xunit;

namespace CoreTests.ErrorHandling.Faults;

public class FaultPublishingPolicyResolveTests
{
    private record Foo;

    [Fact]
    public void resolve_returns_global_defaults_when_no_per_type_override()
    {
        var policy = new FaultPublishingPolicy
        {
            GlobalMode = FaultPublishingMode.DlqOnly,
            // GlobalIncludeExceptionMessage and GlobalIncludeStackTrace
            // default to true; not setting them is intentional.
        };

        var decision = policy.Resolve(typeof(Foo));

        decision.Mode.ShouldBe(FaultPublishingMode.DlqOnly);
        decision.IncludeExceptionMessage.ShouldBeTrue();
        decision.IncludeStackTrace.ShouldBeTrue();
    }

    [Fact]
    public void resolve_global_redaction_flags_propagate_to_decision()
    {
        var policy = new FaultPublishingPolicy
        {
            GlobalMode = FaultPublishingMode.DlqOnly,
            GlobalIncludeExceptionMessage = false,
            GlobalIncludeStackTrace = false,
        };

        var decision = policy.Resolve(typeof(Foo));

        decision.Mode.ShouldBe(FaultPublishingMode.DlqOnly);
        decision.IncludeExceptionMessage.ShouldBeFalse();
        decision.IncludeStackTrace.ShouldBeFalse();
    }

    [Fact]
    public void resolve_per_type_override_takes_precedence_over_globals()
    {
        var policy = new FaultPublishingPolicy
        {
            GlobalMode = FaultPublishingMode.DlqAndDiscard,
            GlobalIncludeExceptionMessage = true,
            GlobalIncludeStackTrace = true,
        };
        policy.SetOverride(
            typeof(Foo),
            FaultPublishingMode.DlqOnly,
            includeExceptionMessage: false,
            includeStackTrace: true);

        var decision = policy.Resolve(typeof(Foo));

        decision.Mode.ShouldBe(FaultPublishingMode.DlqOnly);
        decision.IncludeExceptionMessage.ShouldBeFalse();
        // Override's value (true) is what's stored; not derived from globals.
        decision.IncludeStackTrace.ShouldBeTrue();
    }

    [Fact]
    public void resolve_per_type_None_override_short_circuits_mode_regardless_of_globals()
    {
        var policy = new FaultPublishingPolicy
        {
            GlobalMode = FaultPublishingMode.DlqAndDiscard,
            GlobalIncludeExceptionMessage = true,
            GlobalIncludeStackTrace = true,
        };
        policy.SetOverride(typeof(Foo), FaultPublishingMode.None);

        var decision = policy.Resolve(typeof(Foo));

        decision.Mode.ShouldBe(FaultPublishingMode.None);
        // Even though Mode == None means no fault is ever published (so these
        // values are runtime-inert), Resolve must still return the override's
        // own redaction defaults — not the global values. Pinning this here
        // catches a future regression where Resolve short-circuits on None
        // and accidentally falls through to globals.
        decision.IncludeExceptionMessage.ShouldBeTrue();
        decision.IncludeStackTrace.ShouldBeTrue();
    }
}
