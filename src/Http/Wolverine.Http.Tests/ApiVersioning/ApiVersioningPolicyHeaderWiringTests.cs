using System.Diagnostics;
using Asp.Versioning;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using Microsoft.AspNetCore.Routing;
using Shouldly;
using Wolverine.Http.ApiVersioning;

namespace Wolverine.Http.Tests.ApiVersioning;

// ---------- Handler fixtures ----------

internal class SunsetV1EndpointHandler
{
    [WolverineGet("/sunset-items")]
    [ApiVersion("1.0")]
    public string Get() => "v1 sunset";
}

internal class NoDeprecationV1EndpointHandler
{
    [WolverineGet("/plain-items")]
    [ApiVersion("1.0")]
    public string Get() => "v1 plain";
}

internal class OrdersHeaderTestHandler
{
    [WolverineGet("/orders-header-test")]
    [ApiVersion("1.0")]
    public string Get() => "v1 orders";
}

// ---------- Tests ----------

public class ApiVersioningPolicyHeaderWiringTests
{
    private static void Apply(ApiVersioningPolicy policy, params HttpChain[] chains)
        => policy.Apply(chains, new GenerationRules(), null!);

    // 1 — chain with sunset policy is flagged as needing a header writer; the finalization policy
    // is what actually inserts the writer call at index 0.
    [Fact]
    public void chain_with_sunset_policy_is_flagged_for_header_finalization()
    {
        var date = new DateTimeOffset(2027, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var opts = new WolverineApiVersioningOptions();
        opts.Sunset("1.0").On(date);

        var policy = new ApiVersioningPolicy(opts);
        var chain = HttpChain.ChainFor<SunsetV1EndpointHandler>(x => x.Get());

        Apply(policy, chain);

        policy.ChainsRequiringHeaderEmission.ShouldContain(chain);

        // The writer is *not* yet inserted by ApiVersioningPolicy itself — that is the job of
        // ApiVersionHeaderFinalizationPolicy, registered after every user policy in MapWolverineEndpoints.
        chain.Middleware
            .OfType<MethodCall>()
            .Any(c => c.HandlerType == typeof(ApiVersionHeaderWriter))
            .ShouldBeFalse();

        // Driving the finalization policy directly puts the writer at index 0.
        var finalization = new ApiVersionHeaderFinalizationPolicy(policy.ChainsRequiringHeaderEmission);
        finalization.Apply(new[] { chain }, new GenerationRules(), null!);

        chain.Middleware.OfType<MethodCall>().First()
            .HandlerType.ShouldBe(typeof(ApiVersionHeaderWriter));
    }

    // 2 — chain with all header emit flags disabled and no deprecation/sunset is not flagged.
    [Fact]
    public void chain_with_no_policies_and_emit_supported_disabled_is_not_flagged()
    {
        var opts = new WolverineApiVersioningOptions
        {
            EmitApiSupportedVersionsHeader = false,
            EmitDeprecationHeaders = false
        };

        var policy = new ApiVersioningPolicy(opts);
        var chain = HttpChain.ChainFor<NoDeprecationV1EndpointHandler>(x => x.Get());

        Apply(policy, chain);

        policy.ChainsRequiringHeaderEmission.ShouldNotContain(chain);

        var finalization = new ApiVersionHeaderFinalizationPolicy(policy.ChainsRequiringHeaderEmission);
        finalization.Apply(new[] { chain }, new GenerationRules(), null!);

        chain.Middleware
            .OfType<MethodCall>()
            .Any(c => c.HandlerType == typeof(ApiVersionHeaderWriter))
            .ShouldBeFalse();
    }

    // 3 — ApiVersionEndpointHeaderState metadata is attached to the endpoint after policy runs
    [Fact]
    public void chain_with_state_metadata_attached()
    {
        var date = new DateTimeOffset(2027, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var opts = new WolverineApiVersioningOptions();
        opts.Sunset("1.0").On(date);

        var policy = new ApiVersioningPolicy(opts);
        var chain = HttpChain.ChainFor<SunsetV1EndpointHandler>(x => x.Get());

        Apply(policy, chain);

        var endpoint = chain.BuildEndpoint(RouteWarmup.Lazy);
        var state = endpoint.Metadata.GetMetadata<ApiVersionEndpointHeaderState>();

        state.ShouldNotBeNull();
        state!.Version.ShouldBe(new ApiVersion(1, 0));
        state.Sunset.ShouldNotBeNull();
        state.Sunset!.Date.ShouldBe(date);
    }

    // 4 — finalization is idempotent: re-running does not duplicate the writer when it is already at index 0.
    [Fact]
    public void finalization_is_idempotent_when_writer_already_at_head()
    {
        var opts = new WolverineApiVersioningOptions();
        opts.Sunset("1.0").On(DateTimeOffset.UtcNow.AddDays(30));
        var policy = new ApiVersioningPolicy(opts);
        var chain = HttpChain.ChainFor<OrdersHeaderTestHandler>(x => x.Get());

        Apply(policy, chain);

        var finalization = new ApiVersionHeaderFinalizationPolicy(policy.ChainsRequiringHeaderEmission);
        finalization.Apply(new[] { chain }, new GenerationRules(), null!);
        var firstCount = chain.Middleware.OfType<MethodCall>().Count(c => c.HandlerType == typeof(ApiVersionHeaderWriter));

        finalization.Apply(new[] { chain }, new GenerationRules(), null!);
        var secondCount = chain.Middleware.OfType<MethodCall>().Count(c => c.HandlerType == typeof(ApiVersionHeaderWriter));

        firstCount.ShouldBe(1);
        secondCount.ShouldBe(1);
        chain.Middleware.OfType<MethodCall>().First()
            .HandlerType.ShouldBe(typeof(ApiVersionHeaderWriter));
    }

#if DEBUG
    // 5 — DEBUG-only: writer displaced from index 0 trips the Debug.Assert invariant on the
    // second Apply. Pins the contract that no other policy is permitted to push the writer below
    // index 0 once finalization has positioned it. RELEASE builds compile out the assert, so the
    // test only runs under DEBUG (the catch-all #if guard makes the whole test invisible to xunit
    // in RELEASE — silent skip is acceptable since the assert itself is also no-op).
    [Fact]
    public void finalization_assert_fires_when_writer_was_displaced()
    {
        var opts = new WolverineApiVersioningOptions();
        opts.Sunset("1.0").On(DateTimeOffset.UtcNow.AddDays(30));
        var policy = new ApiVersioningPolicy(opts);
        var chain = HttpChain.ChainFor<OrdersHeaderTestHandler>(x => x.Get());

        Apply(policy, chain);

        var finalization = new ApiVersionHeaderFinalizationPolicy(policy.ChainsRequiringHeaderEmission);
        finalization.Apply(new[] { chain }, new GenerationRules(), null!);

        // Manually displace the writer: insert any other frame at index 0 so the writer drifts to index 1.
        // The intentional precondition violation that the second Apply call must catch.
        var displacer = MethodCall.For<OrdersHeaderTestHandler>(x => x.Get());
        chain.Middleware.Insert(0, displacer);
        chain.Middleware.IndexOf(chain.Middleware.OfType<MethodCall>().First(c => c.HandlerType == typeof(ApiVersionHeaderWriter)))
            .ShouldBe(1);

        // Swap in a throwing trace listener so Debug.Assert(false) raises instead of popping a UI dialog
        // or silently writing to the trace stream.
        var originalListeners = new TraceListener[Trace.Listeners.Count];
        Trace.Listeners.CopyTo(originalListeners, 0);
        Trace.Listeners.Clear();
        Trace.Listeners.Add(new ThrowingTraceListener());

        try
        {
            Should.Throw<DebugAssertException>(() =>
                finalization.Apply(new[] { chain }, new GenerationRules(), null!));
        }
        finally
        {
            Trace.Listeners.Clear();
            foreach (var listener in originalListeners)
                Trace.Listeners.Add(listener);
        }
    }

    private sealed class DebugAssertException : Exception
    {
        public DebugAssertException(string message) : base(message) { }
    }

    private sealed class ThrowingTraceListener : TraceListener
    {
        public override void Write(string? message) { }
        public override void WriteLine(string? message) { }

        public override void Fail(string? message)
            => throw new DebugAssertException(message ?? "Debug.Assert failed");

        public override void Fail(string? message, string? detailMessage)
            => throw new DebugAssertException($"{message} :: {detailMessage}");
    }
#endif
}
