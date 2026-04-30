using Asp.Versioning;
using JasperFx.CodeGeneration;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Shouldly;
using Wolverine.Http.ApiVersioning;

namespace Wolverine.Http.Tests.ApiVersioning;

// ---------- Test handler fixtures ----------

internal class OrdersV1Handler
{
    [WolverineGet("/orders")]
    [ApiVersion("1.0")]
    public string Get() => "v1";
}

internal class OrdersV2Handler
{
    [WolverineGet("/orders")]
    [ApiVersion("2.0")]
    public string Get() => "v2";
}

internal class OrdersV1DuplicateHandler
{
    [WolverineGet("/orders")]
    [ApiVersion("1.0")]
    public string Get() => "v1-dup";
}

internal class UnversionedOrdersHandler
{
    [WolverineGet("/orders")]
    public string Get() => "unversioned";
}

internal class DeprecatedV1Handler
{
    [WolverineGet("/items")]
    [ApiVersion("1.0", Deprecated = true)]
    public string Get() => "deprecated";
}

// ---------- Tests ----------

public class ApiVersioningPolicyTests
{

    private static void Apply(ApiVersioningPolicy policy, params HttpChain[] chains)
        => policy.Apply(chains, new GenerationRules(), null!);

    // 1
    [Fact]
    public void attribute_resolves_version_onto_chain()
    {
        var opts = new WolverineApiVersioningOptions();
        var policy = new ApiVersioningPolicy(opts);
        var chain = HttpChain.ChainFor<OrdersV1Handler>(x => x.Get());

        Apply(policy, chain);

        chain.ApiVersion.ShouldBe(new ApiVersion(1, 0));
    }

    // 2
    [Fact]
    public void versioned_chain_has_url_segment_prefixed()
    {
        var opts = new WolverineApiVersioningOptions(); // default UrlSegmentPrefix = "v{version}"
        var policy = new ApiVersioningPolicy(opts);
        var chain = HttpChain.ChainFor<OrdersV1Handler>(x => x.Get());

        Apply(policy, chain);

        chain.RoutePattern!.RawText.ShouldBe("/v1/orders");
    }

    // 3
    [Fact]
    public void unversioned_passthrough_leaves_chain_alone()
    {
        var opts = new WolverineApiVersioningOptions { UnversionedPolicy = UnversionedPolicy.PassThrough };
        var policy = new ApiVersioningPolicy(opts);
        var chain = HttpChain.ChainFor<UnversionedOrdersHandler>(x => x.Get());
        var originalRoute = chain.RoutePattern!.RawText;

        Apply(policy, chain);

        chain.ApiVersion.ShouldBeNull();
        chain.RoutePattern!.RawText.ShouldBe(originalRoute);
    }

    // 4
    [Fact]
    public void unversioned_require_explicit_throws()
    {
        var opts = new WolverineApiVersioningOptions { UnversionedPolicy = UnversionedPolicy.RequireExplicit };
        var policy = new ApiVersioningPolicy(opts);
        var chain = HttpChain.ChainFor<UnversionedOrdersHandler>(x => x.Get());
        chain.DisplayName = "GET /orders (unversioned)";

        var ex = Should.Throw<InvalidOperationException>(() => Apply(policy, chain));
        ex.Message.ShouldContain("GET /orders (unversioned)");
    }

    // 5
    [Fact]
    public void unversioned_assign_default_uses_default_version()
    {
        var opts = new WolverineApiVersioningOptions
        {
            UnversionedPolicy = UnversionedPolicy.AssignDefault,
            DefaultVersion = new ApiVersion(1, 0)
        };
        var policy = new ApiVersioningPolicy(opts);
        var chain = HttpChain.ChainFor<UnversionedOrdersHandler>(x => x.Get());

        Apply(policy, chain);

        chain.ApiVersion.ShouldBe(new ApiVersion(1, 0));
        chain.RoutePattern!.RawText.ShouldBe("/v1/orders");
    }

    // 6
    [Fact]
    public void assign_default_without_default_version_throws()
    {
        var opts = new WolverineApiVersioningOptions
        {
            UnversionedPolicy = UnversionedPolicy.AssignDefault,
            DefaultVersion = null
        };
        var policy = new ApiVersioningPolicy(opts);
        var chain = HttpChain.ChainFor<UnversionedOrdersHandler>(x => x.Get());

        var ex = Should.Throw<InvalidOperationException>(() => Apply(policy, chain));
        ex.Message.ShouldContain("DefaultVersion must be set");
    }

    // 7
    [Fact]
    public void sunset_policy_attached_from_options()
    {
        var date = new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var opts = new WolverineApiVersioningOptions();
        opts.Sunset("1.0").On(date);

        var policy = new ApiVersioningPolicy(opts);
        var chain = HttpChain.ChainFor<OrdersV1Handler>(x => x.Get());

        Apply(policy, chain);

        chain.SunsetPolicy.ShouldNotBeNull();
        chain.SunsetPolicy!.Date.ShouldBe(date);
    }

    // 8
    [Fact]
    public void deprecation_attribute_marks_chain_deprecated()
    {
        var opts = new WolverineApiVersioningOptions();
        var policy = new ApiVersioningPolicy(opts);
        var chain = HttpChain.ChainFor<DeprecatedV1Handler>(x => x.Get());

        Apply(policy, chain);

        chain.DeprecationPolicy.ShouldNotBeNull();
    }

    // 9
    [Fact]
    public void attribute_deprecation_takes_precedence_over_options()
    {
        var otherDate = new DateTimeOffset(2028, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var opts = new WolverineApiVersioningOptions();
        opts.Deprecate("1.0").On(otherDate);

        var policy = new ApiVersioningPolicy(opts);
        var chain = HttpChain.ChainFor<DeprecatedV1Handler>(x => x.Get());

        Apply(policy, chain);

        chain.DeprecationPolicy.ShouldNotBeNull();
        chain.DeprecationPolicy!.Date.ShouldBeNull();  // attribute-driven, no date
    }

    // 10
    [Fact]
    public void duplicate_route_and_version_throws()
    {
        var opts = new WolverineApiVersioningOptions();
        var policy = new ApiVersioningPolicy(opts);
        var chain1 = HttpChain.ChainFor<OrdersV1Handler>(x => x.Get());
        var chain2 = HttpChain.ChainFor<OrdersV1DuplicateHandler>(x => x.Get());
        chain1.DisplayName = "OrdersV1Handler";
        chain2.DisplayName = "OrdersV1DuplicateHandler";

        var ex = Should.Throw<InvalidOperationException>(() => Apply(policy, chain1, chain2));
        ex.Message.ShouldContain("GET");
        ex.Message.ShouldContain("/orders");
        ex.Message.ShouldContain("1.0");
        ex.Message.ShouldContain("OrdersV1Handler");
        ex.Message.ShouldContain("OrdersV1DuplicateHandler");
    }

    // 11
    [Fact]
    public void duplicate_route_with_different_versions_does_not_throw()
    {
        var opts = new WolverineApiVersioningOptions();
        var policy = new ApiVersioningPolicy(opts);
        var chain1 = HttpChain.ChainFor<OrdersV1Handler>(x => x.Get());
        var chain2 = HttpChain.ChainFor<OrdersV2Handler>(x => x.Get());

        Should.NotThrow(() => Apply(policy, chain1, chain2));
    }

    // 12 — group-name metadata attached
    [Fact]
    public void group_name_metadata_attached()
    {
        var opts = new WolverineApiVersioningOptions();
        var policy = new ApiVersioningPolicy(opts);
        var chain = HttpChain.ChainFor<OrdersV1Handler>(x => x.Get());

        Apply(policy, chain);

        // BuildEndpoint commits all metadata callbacks to the RouteEndpointBuilder.
        var endpoint = chain.BuildEndpoint(RouteWarmup.Lazy);

        var groupMeta = endpoint.Metadata.GetMetadata<IEndpointGroupNameMetadata>();
        groupMeta.ShouldNotBeNull();
        groupMeta!.EndpointGroupName.ShouldBe("v1");
    }

    // 13 — ApiVersionMetadata attached
    [Fact]
    public void apiversion_metadata_attached()
    {
        var opts = new WolverineApiVersioningOptions();
        var policy = new ApiVersioningPolicy(opts);
        var chain = HttpChain.ChainFor<OrdersV1Handler>(x => x.Get());

        Apply(policy, chain);

        var endpoint = chain.BuildEndpoint(RouteWarmup.Lazy);

        var versionMeta = endpoint.Metadata.GetMetadata<ApiVersionMetadata>();
        versionMeta.ShouldNotBeNull();
        versionMeta!.IsApiVersionNeutral.ShouldBeFalse();
        versionMeta.Map(ApiVersionMapping.Explicit).ImplementedApiVersions.ShouldContain(new ApiVersion(1, 0));
    }

    // 14 — UrlSegmentPrefix validation
    [Fact]
    public void url_segment_prefix_without_version_token_throws()
    {
        var opts = new WolverineApiVersioningOptions { UrlSegmentPrefix = "api" };
        var policy = new ApiVersioningPolicy(opts);
        var chain = HttpChain.ChainFor<OrdersV1Handler>(x => x.Get());

        Should.Throw<InvalidOperationException>(() =>
            policy.Apply(new[] { chain }, new GenerationRules(), null!))
            .Message.ShouldContain("{version}");
    }

    // 15 — Idempotency: apply() called twice should not double-prefix
    [Fact]
    public void apply_twice_is_idempotent()
    {
        var opts = new WolverineApiVersioningOptions();
        var policy = new ApiVersioningPolicy(opts);
        var chain = HttpChain.ChainFor<OrdersV1Handler>(x => x.Get());

        policy.Apply(new[] { chain }, new GenerationRules(), null!);
        var routeAfterFirstApply = chain.RoutePattern!.RawText;

        policy.Apply(new[] { chain }, new GenerationRules(), null!);

        chain.RoutePattern!.RawText.ShouldBe(routeAfterFirstApply);
    }

    // 16 — Custom document name strategy drives group name
    [Fact]
    public void custom_document_name_strategy_drives_group_name()
    {
        var opts = new WolverineApiVersioningOptions();
        opts.OpenApi.DocumentNameStrategy = v => $"api-v{v.MajorVersion}";
        var policy = new ApiVersioningPolicy(opts);
        var chain = HttpChain.ChainFor<OrdersV1Handler>(x => x.Get());

        policy.Apply(new[] { chain }, new GenerationRules(), null!);
        var endpoint = chain.BuildEndpoint(RouteWarmup.Lazy);

        endpoint.Metadata.GetMetadata<IEndpointGroupNameMetadata>()!.EndpointGroupName.ShouldBe("api-v1");
    }

}
