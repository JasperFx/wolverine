using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.Runtime;
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

    [Fact]
    public void set_override_succeeds_before_freeze()
    {
        var policy = new FaultPublishingPolicy();
        policy.SetOverride(typeof(Foo), FaultPublishingMode.DlqOnly);

        var decision = policy.Resolve(typeof(Foo));
        decision.Mode.ShouldBe(FaultPublishingMode.DlqOnly);
    }

    [Fact]
    public void set_override_throws_after_freeze()
    {
        var policy = new FaultPublishingPolicy();
        policy.Freeze();

        var ex = Should.Throw<InvalidOperationException>(() =>
            policy.SetOverride(typeof(Foo), FaultPublishingMode.DlqOnly));
        ex.Message.ShouldContain("frozen");
    }

    [Fact]
    public async Task wolverine_runtime_freezes_fault_publishing_policy_at_startup()
    {
        using var host = await Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .UseWolverine(opts => opts.PublishFaultEvents())
            .StartAsync();

        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();
        Should.Throw<InvalidOperationException>(() =>
            runtime.Options.FaultPublishing.SetOverride(typeof(Foo), FaultPublishingMode.DlqOnly));
    }

    [Fact]
    public void override_value_is_visible_after_freeze()
    {
        // Pins the pre-Freeze→post-Freeze handover: the FrozenDictionary
        // snapshot built inside Freeze() must contain every override that
        // was written before Freeze.
        var policy = new FaultPublishingPolicy();
        policy.SetOverride(
            typeof(Foo),
            FaultPublishingMode.DlqOnly,
            includeExceptionMessage: false,
            includeStackTrace: false);
        policy.Freeze();

        var decision = policy.Resolve(typeof(Foo));

        decision.Mode.ShouldBe(FaultPublishingMode.DlqOnly);
        decision.IncludeExceptionMessage.ShouldBeFalse();
        decision.IncludeStackTrace.ShouldBeFalse();
    }
}
