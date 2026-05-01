using Asp.Versioning;
using JasperFx.CodeGeneration;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Shouldly;
using Wolverine.Http.ApiVersioning;

namespace Wolverine.Http.Tests.ApiVersioning;

// ---------- Test handler fixtures ----------

internal class TwoVersionMethodOrdersHandler
{
    [WolverineGet("/orders")]
    [ApiVersion("1.0")]
    [ApiVersion("2.0")]
    public string Get() => "two-versions";
}

[ApiVersion("1.0")]
[ApiVersion("2.0")]
internal class ClassLevelTwoVersionOrdersHandler
{
    [WolverineGet("/items")]
    public string Get() => "class-two-versions";
}

[ApiVersion("1.0")]
[ApiVersion("2.0")]
[ApiVersion("3.0")]
internal class ClassThreeMapToTwoOrdersHandler
{
    [WolverineGet("/widgets")]
    [MapToApiVersion("2.0")]
    public string Get() => "filtered";
}

internal class TwoVersionWithPerVersionDeprecationHandler
{
    [WolverineGet("/legacy")]
    [ApiVersion("1.0", Deprecated = true)]
    [ApiVersion("2.0")]
    public string Get() => "per-version-deprecation";
}

[ApiVersion("1.0")]
internal class MapToInvalidVersionHandler
{
    [WolverineGet("/missing")]
    [MapToApiVersion("3.0")]
    public string Get() => "missing";
}

[ApiVersion("1.0")]
internal class BothAttrsHandler
{
    [WolverineGet("/both")]
    [ApiVersion("2.0")]
    [MapToApiVersion("1.0")]
    public string Get() => "both";
}

public class MultiVersionExpansionTests
{
    private static void Apply(ApiVersioningPolicy policy, params HttpChain[] chains)
        => policy.Apply(chains, new GenerationRules(), null!);

    private static List<HttpChain> ListOf(params HttpChain[] chains) => new(chains);

    // 1 — multi-version method produces N versioned routes after expansion + policy.
    [Fact]
    public void method_level_two_versions_expand_to_two_routes()
    {
        var chains = ListOf(HttpChain.ChainFor<TwoVersionMethodOrdersHandler>(x => x.Get()));
        MultiVersionExpansion.Expand(chains);

        chains.Count.ShouldBe(2);
        chains.Select(c => c.ApiVersion).ShouldBe(new[] { new ApiVersion(1, 0), new ApiVersion(2, 0) });

        var policy = new ApiVersioningPolicy(new WolverineApiVersioningOptions());
        policy.Apply(chains, new GenerationRules(), null!);

        chains.Select(c => c.RoutePattern!.RawText).OrderBy(s => s).ShouldBe(new[] { "/v1/orders", "/v2/orders" });
    }

    // 2 — class-level [ApiVersion] list with method-level [MapToApiVersion] keeps only the listed subset.
    [Fact]
    public void mapto_filters_to_only_the_listed_version()
    {
        var chains = ListOf(HttpChain.ChainFor<ClassThreeMapToTwoOrdersHandler>(x => x.Get()));
        MultiVersionExpansion.Expand(chains);

        chains.Count.ShouldBe(1);
        chains[0].ApiVersion.ShouldBe(new ApiVersion(2, 0));

        var policy = new ApiVersioningPolicy(new WolverineApiVersioningOptions());
        policy.Apply(chains, new GenerationRules(), null!);

        chains[0].RoutePattern!.RawText.ShouldBe("/v2/widgets");
    }

    // 3 — class with [ApiVersion("1.0")] + method [MapToApiVersion("3.0")] fails fast at expansion.
    [Fact]
    public void mapto_for_unknown_class_version_throws_at_expansion_time()
    {
        var chains = ListOf(HttpChain.ChainFor<MapToInvalidVersionHandler>(x => x.Get()));

        var ex = Should.Throw<InvalidOperationException>(() => MultiVersionExpansion.Expand(chains));
        ex.Message.ShouldContain("MapToApiVersion");
        ex.Message.ShouldContain("MapToInvalidVersionHandler");
        ex.Message.ShouldContain("3.0");
    }

    // 4 — both [ApiVersion] and [MapToApiVersion] on the same method fails fast at expansion.
    [Fact]
    public void both_apiversion_and_mapto_throws_at_expansion_time()
    {
        var chains = ListOf(HttpChain.ChainFor<BothAttrsHandler>(x => x.Get()));

        var ex = Should.Throw<InvalidOperationException>(() => MultiVersionExpansion.Expand(chains));
        ex.Message.ShouldContain("[ApiVersion]");
        ex.Message.ShouldContain("[MapToApiVersion]");
    }

    // 5 — per-version deprecation propagates only to the deprecated clone.
    [Fact]
    public void per_version_deprecation_applies_independently_across_clones()
    {
        var chains = ListOf(HttpChain.ChainFor<TwoVersionWithPerVersionDeprecationHandler>(x => x.Get()));
        MultiVersionExpansion.Expand(chains);

        chains.Count.ShouldBe(2);
        var v1 = chains.Single(c => c.ApiVersion == new ApiVersion(1, 0));
        var v2 = chains.Single(c => c.ApiVersion == new ApiVersion(2, 0));

        v1.DeprecationPolicy.ShouldNotBeNull();
        v2.DeprecationPolicy.ShouldBeNull();
    }

    // 6 — class-level multi-version with no method override expands per class versions.
    [Fact]
    public void class_level_two_versions_expand_to_two_routes()
    {
        var chains = ListOf(HttpChain.ChainFor<ClassLevelTwoVersionOrdersHandler>(x => x.Get()));
        MultiVersionExpansion.Expand(chains);

        chains.Count.ShouldBe(2);
        chains.Select(c => c.ApiVersion).ShouldBe(new[] { new ApiVersion(1, 0), new ApiVersion(2, 0) });
    }

    // 7 — versioned-route metadata wires up per clone with distinct ApiVersionMetadata.
    [Fact]
    public void per_version_metadata_attached_to_each_clone()
    {
        var chains = ListOf(HttpChain.ChainFor<TwoVersionMethodOrdersHandler>(x => x.Get()));
        MultiVersionExpansion.Expand(chains);

        var policy = new ApiVersioningPolicy(new WolverineApiVersioningOptions());
        policy.Apply(chains, new GenerationRules(), null!);

        foreach (var chain in chains)
        {
            var endpoint = chain.BuildEndpoint(RouteWarmup.Lazy);
            var versionMeta = endpoint.Metadata.GetMetadata<ApiVersionMetadata>();
            versionMeta.ShouldNotBeNull();
            versionMeta!.Map(ApiVersionMapping.Explicit).ImplementedApiVersions.ShouldContain(chain.ApiVersion!);
        }
    }
}
