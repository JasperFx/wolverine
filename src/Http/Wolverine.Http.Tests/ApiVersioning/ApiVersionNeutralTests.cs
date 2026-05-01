using Asp.Versioning;
using JasperFx.CodeGeneration;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Shouldly;
using Wolverine.Http.ApiVersioning;

namespace Wolverine.Http.Tests.ApiVersioning;

// ---------- Test handler fixtures ----------

[ApiVersionNeutral]
internal class HealthHandler
{
    [WolverineGet("/health")]
    public string Get() => "ok";
}

[ApiVersion("1.0")]
internal class MixedVersionedHandler
{
    [WolverineGet("/customers")]
    public string GetCustomers() => "v1-customers";

    [ApiVersionNeutral]
    [WolverineGet("/customers/ping")]
    public string Ping() => "pong";
}

[ApiVersion("1.0")]
[ApiVersionNeutral]
internal class ConflictingClassHandler
{
    [WolverineGet("/conflict-class")]
    public string Get() => "conflict";
}

internal class ConflictingMethodHandler
{
    [ApiVersion("1.0")]
    [ApiVersionNeutral]
    [WolverineGet("/conflict-method")]
    public string Get() => "conflict";
}

[ApiVersion("1.0")]
internal class HealthVersionedHandler
{
    [WolverineGet("/health")]
    public string Get() => "v1-health";
}

// ---------- Tests ----------

public class ApiVersionNeutralTests
{
    private static void Apply(ApiVersioningPolicy policy, params HttpChain[] chains)
        => policy.Apply(chains, new GenerationRules(), null!);

    // 1 — class-level neutrality: no rewrite, no version, ApiVersion stays null
    [Fact]
    public void class_level_neutral_does_not_rewrite_route()
    {
        var opts = new WolverineApiVersioningOptions();
        var policy = new ApiVersioningPolicy(opts);
        var chain = HttpChain.ChainFor<HealthHandler>(x => x.Get());
        var originalRoute = chain.RoutePattern!.RawText;

        Apply(policy, chain);

        chain.IsApiVersionNeutral.ShouldBeTrue();
        chain.ApiVersion.ShouldBeNull();
        chain.RoutePattern!.RawText.ShouldBe(originalRoute);
    }

    // 1b — class-level neutrality: ApiVersionMetadata.IsApiVersionNeutral is true
    [Fact]
    public void class_level_neutral_attaches_neutral_apiversion_metadata()
    {
        var opts = new WolverineApiVersioningOptions();
        var policy = new ApiVersioningPolicy(opts);
        var chain = HttpChain.ChainFor<HealthHandler>(x => x.Get());

        Apply(policy, chain);

        var endpoint = chain.BuildEndpoint(RouteWarmup.Lazy);
        var versionMeta = endpoint.Metadata.GetMetadata<ApiVersionMetadata>();
        versionMeta.ShouldNotBeNull();
        versionMeta!.IsApiVersionNeutral.ShouldBeTrue();
    }

    // 1c — neutral chains do not get a group name (so they fall into the default OpenAPI doc).
    [Fact]
    public void class_level_neutral_attaches_no_group_name()
    {
        var opts = new WolverineApiVersioningOptions();
        var policy = new ApiVersioningPolicy(opts);
        var chain = HttpChain.ChainFor<HealthHandler>(x => x.Get());

        Apply(policy, chain);

        var endpoint = chain.BuildEndpoint(RouteWarmup.Lazy);
        endpoint.Metadata.GetMetadata<IEndpointGroupNameMetadata>().ShouldBeNull();
    }

    // 1d — neutral chains do not get sunset/deprecation/api-supported-versions header writers.
    [Fact]
    public void class_level_neutral_does_not_get_header_state_metadata()
    {
        var opts = new WolverineApiVersioningOptions { EmitApiSupportedVersionsHeader = true };
        var policy = new ApiVersioningPolicy(opts);
        var chain = HttpChain.ChainFor<HealthHandler>(x => x.Get());

        Apply(policy, chain);

        var endpoint = chain.BuildEndpoint(RouteWarmup.Lazy);
        endpoint.Metadata.GetMetadata<ApiVersionEndpointHeaderState>().ShouldBeNull();
    }

    // 2 — method-level neutrality inside an [ApiVersion] class only affects that one method
    [Fact]
    public void method_level_neutral_inside_versioned_class_only_affects_that_method()
    {
        var opts = new WolverineApiVersioningOptions();
        var policy = new ApiVersioningPolicy(opts);

        var pingChain = HttpChain.ChainFor<MixedVersionedHandler>(x => x.Ping());
        var customersChain = HttpChain.ChainFor<MixedVersionedHandler>(x => x.GetCustomers());

        Apply(policy, pingChain, customersChain);

        pingChain.IsApiVersionNeutral.ShouldBeTrue();
        pingChain.ApiVersion.ShouldBeNull();
        pingChain.RoutePattern!.RawText.ShouldBe("/customers/ping");

        customersChain.IsApiVersionNeutral.ShouldBeFalse();
        customersChain.ApiVersion.ShouldBe(new ApiVersion(1, 0));
        customersChain.RoutePattern!.RawText.ShouldBe("/v1/customers");
    }

    // 3 — neutral chain satisfies RequireExplicit
    [Fact]
    public void neutral_chain_satisfies_require_explicit_policy()
    {
        var opts = new WolverineApiVersioningOptions { UnversionedPolicy = UnversionedPolicy.RequireExplicit };
        var policy = new ApiVersioningPolicy(opts);
        var chain = HttpChain.ChainFor<HealthHandler>(x => x.Get());

        Should.NotThrow(() => Apply(policy, chain));
    }

    // 4 — class-level conflict: [ApiVersion] + [ApiVersionNeutral] on same class throws
    [Fact]
    public void conflicting_attributes_on_class_throws()
    {
        var opts = new WolverineApiVersioningOptions();
        var policy = new ApiVersioningPolicy(opts);
        var chain = HttpChain.ChainFor<ConflictingClassHandler>(x => x.Get());

        var ex = Should.Throw<InvalidOperationException>(() => Apply(policy, chain));
        ex.Message.ShouldContain(nameof(ConflictingClassHandler));
        ex.Message.ShouldContain("[ApiVersion]");
        ex.Message.ShouldContain("[ApiVersionNeutral]");
    }

    // 4b — method-level conflict: [ApiVersion] + [ApiVersionNeutral] on same method throws
    [Fact]
    public void conflicting_attributes_on_method_throws()
    {
        var opts = new WolverineApiVersioningOptions();
        var policy = new ApiVersioningPolicy(opts);
        var chain = HttpChain.ChainFor<ConflictingMethodHandler>(x => x.Get());

        var ex = Should.Throw<InvalidOperationException>(() => Apply(policy, chain));
        ex.Message.ShouldContain("[ApiVersion]");
        ex.Message.ShouldContain("[ApiVersionNeutral]");
    }

    // 5 — neutral chain skipped from duplicate-detection on the version axis: a versioned chain
    // and a neutral chain at the same (verb, route) do not collide.
    [Fact]
    public void neutral_chain_does_not_participate_in_duplicate_detection()
    {
        var opts = new WolverineApiVersioningOptions { UrlSegmentPrefix = null };
        var policy = new ApiVersioningPolicy(opts);

        var neutralChain = HttpChain.ChainFor<HealthHandler>(x => x.Get());
        var versionedAtSameRoute = HttpChain.ChainFor<HealthVersionedHandler>(x => x.Get());

        Should.NotThrow(() => Apply(policy, neutralChain, versionedAtSameRoute));
    }
}
