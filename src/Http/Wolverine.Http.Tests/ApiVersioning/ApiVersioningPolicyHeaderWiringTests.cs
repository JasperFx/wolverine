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

    // 1 — chain with sunset policy gets ApiVersionHeaderWriter postprocessor
    [Fact]
    public void chain_with_sunset_policy_gets_header_writer_postprocessor()
    {
        var date = new DateTimeOffset(2027, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var opts = new WolverineApiVersioningOptions();
        opts.Sunset("1.0").On(date);

        var policy = new ApiVersioningPolicy(opts);
        var chain = HttpChain.ChainFor<SunsetV1EndpointHandler>(x => x.Get());

        Apply(policy, chain);

        chain.Postprocessors
            .OfType<MethodCall>()
            .Any(c => c.HandlerType == typeof(ApiVersionHeaderWriter))
            .ShouldBeTrue();
    }

    // 2 — chain with all header emit flags disabled and no deprecation/sunset gets no postprocessor
    [Fact]
    public void chain_with_no_policies_and_emit_supported_disabled_gets_no_postprocessor()
    {
        var opts = new WolverineApiVersioningOptions
        {
            EmitApiSupportedVersionsHeader = false,
            EmitDeprecationHeaders = false
        };

        var policy = new ApiVersioningPolicy(opts);
        var chain = HttpChain.ChainFor<NoDeprecationV1EndpointHandler>(x => x.Get());

        Apply(policy, chain);

        chain.Postprocessors
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

    // 4 — apply twice does not add duplicate header postprocessor (idempotency guard)
    [Fact]
    public void apply_twice_does_not_add_duplicate_header_postprocessor()
    {
        var opts = new WolverineApiVersioningOptions();
        opts.Sunset("1.0").On(DateTimeOffset.UtcNow.AddDays(30));
        var policy = new ApiVersioningPolicy(opts);
        var chain = HttpChain.ChainFor<OrdersHeaderTestHandler>(x => x.Get());

        Apply(policy, chain);
        var firstCount = chain.Postprocessors.OfType<MethodCall>().Count(c => c.HandlerType == typeof(ApiVersionHeaderWriter));

        Apply(policy, chain);
        var secondCount = chain.Postprocessors.OfType<MethodCall>().Count(c => c.HandlerType == typeof(ApiVersionHeaderWriter));

        secondCount.ShouldBe(firstCount);
    }
}
