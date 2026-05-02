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

// Two handlers that, after expansion, both serve (GET, /reports, v2.0) — duplicate detection target.
internal class FirstReportsMultiVersionHandler
{
    [WolverineGet("/reports")]
    [ApiVersion("1.0")]
    [ApiVersion("2.0")]
    public string Get() => "first";
}

internal class SecondReportsMultiVersionHandler
{
    [WolverineGet("/reports")]
    [ApiVersion("2.0")]
    [ApiVersion("3.0")]
    public string Get() => "second";
}

// Plain unversioned handler — must survive expansion without any ApiVersion side effects.
internal class UnversionedPlainHandler
{
    [WolverineGet("/health")]
    public string Get() => "ok";
}

// Date-based version handler used to assert OperationId suffix sanitisation.
internal class DateBasedVersionHandler
{
    [WolverineGet("/dated")]
    [ApiVersion("2024-01-01")]
    [ApiVersion("2025-06-15")]
    public string Get() => "dated";
}

public class MultiVersionExpansionTests
{
    private static List<HttpChain> ListOf(params HttpChain[] chains) => new(chains);

    // 1 — multi-version method produces N versioned routes after expansion + policy.
    [Fact]
    public void method_level_two_versions_expand_to_two_routes()
    {
        var chains = ListOf(HttpChain.ChainFor<TwoVersionMethodOrdersHandler>(x => x.Get()));
        MultiVersionExpansion.ExpandInPlace(chains);

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
        MultiVersionExpansion.ExpandInPlace(chains);

        // Single-version chains are NOT expanded — ApiVersioningPolicy resolves them in ResolveAttributes.
        chains.Count.ShouldBe(1);

        var policy = new ApiVersioningPolicy(new WolverineApiVersioningOptions());
        policy.Apply(chains, new GenerationRules(), null!);

        chains[0].ApiVersion.ShouldBe(new ApiVersion(2, 0));
        chains[0].RoutePattern!.RawText.ShouldBe("/v2/widgets");
    }

    // 3 — class with [ApiVersion("1.0")] + method [MapToApiVersion("3.0")] fails fast at expansion.
    [Fact]
    public void mapto_for_unknown_class_version_throws_at_expansion_time()
    {
        var chains = ListOf(HttpChain.ChainFor<MapToInvalidVersionHandler>(x => x.Get()));

        var ex = Should.Throw<InvalidOperationException>(() => MultiVersionExpansion.ExpandInPlace(chains));
        ex.Message.ShouldContain("MapToApiVersion");
        ex.Message.ShouldContain("MapToInvalidVersionHandler");
        ex.Message.ShouldContain("3.0");
    }

    // 4 — both [ApiVersion] and [MapToApiVersion] on the same method fails fast at expansion.
    [Fact]
    public void both_apiversion_and_mapto_throws_at_expansion_time()
    {
        var chains = ListOf(HttpChain.ChainFor<BothAttrsHandler>(x => x.Get()));

        var ex = Should.Throw<InvalidOperationException>(() => MultiVersionExpansion.ExpandInPlace(chains));
        ex.Message.ShouldContain("[ApiVersion]");
        ex.Message.ShouldContain("[MapToApiVersion]");
    }

    // 5 — per-version deprecation propagates only to the deprecated clone.
    [Fact]
    public void per_version_deprecation_applies_independently_across_clones()
    {
        var chains = ListOf(HttpChain.ChainFor<TwoVersionWithPerVersionDeprecationHandler>(x => x.Get()));
        MultiVersionExpansion.ExpandInPlace(chains);

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
        MultiVersionExpansion.ExpandInPlace(chains);

        chains.Count.ShouldBe(2);
        chains.Select(c => c.ApiVersion).ShouldBe(new[] { new ApiVersion(1, 0), new ApiVersion(2, 0) });
    }

    // 7 — versioned-route metadata wires up per clone with the union of sibling versions in supported.
    [Fact]
    public void per_version_metadata_attached_to_each_clone_advertises_sibling_versions()
    {
        var chains = ListOf(HttpChain.ChainFor<TwoVersionMethodOrdersHandler>(x => x.Get()));
        MultiVersionExpansion.ExpandInPlace(chains);

        var policy = new ApiVersioningPolicy(new WolverineApiVersioningOptions());
        policy.Apply(chains, new GenerationRules(), null!);

        var allVersions = new[] { new ApiVersion(1, 0), new ApiVersion(2, 0) };

        foreach (var chain in chains)
        {
            var endpoint = chain.BuildEndpoint(RouteWarmup.Lazy);
            var versionMeta = endpoint.Metadata.GetMetadata<ApiVersionMetadata>();
            versionMeta.ShouldNotBeNull();

            var model = versionMeta!.Map(ApiVersionMapping.Explicit);

            // Each clone declares only its own version, but advertises both as supported so the
            // api-supported-versions response header reports the full sibling set.
            model.DeclaredApiVersions.ShouldBe(new[] { chain.ApiVersion! });
            model.SupportedApiVersions.OrderBy(v => v).ShouldBe(allVersions);
            model.ImplementedApiVersions.OrderBy(v => v).ShouldBe(allVersions);
        }
    }

    // 8 — per-version deprecation reflects in supported / deprecated split of the model.
    [Fact]
    public void per_version_metadata_reports_deprecated_subset_correctly()
    {
        var chains = ListOf(HttpChain.ChainFor<TwoVersionWithPerVersionDeprecationHandler>(x => x.Get()));
        MultiVersionExpansion.ExpandInPlace(chains);

        var policy = new ApiVersioningPolicy(new WolverineApiVersioningOptions());
        policy.Apply(chains, new GenerationRules(), null!);

        foreach (var chain in chains)
        {
            var endpoint = chain.BuildEndpoint(RouteWarmup.Lazy);
            var model = endpoint.Metadata.GetMetadata<ApiVersionMetadata>()!.Map(ApiVersionMapping.Explicit);

            model.SupportedApiVersions.ShouldBe(new[] { new ApiVersion(2, 0) });
            model.DeprecatedApiVersions.ShouldBe(new[] { new ApiVersion(1, 0) });
        }
    }

    // 9 — Mandate M3: each clone's endpoint metadata exposes only its own [ApiVersion] attribute.
    // OpenAPI tooling that walks endpoint metadata must not see foreign versions, otherwise it
    // reports each clone as implementing every sibling version.
    [Fact]
    public void each_clone_endpoint_metadata_only_lists_its_own_apiversion_attribute()
    {
        var chains = ListOf(HttpChain.ChainFor<TwoVersionMethodOrdersHandler>(x => x.Get()));
        MultiVersionExpansion.ExpandInPlace(chains);

        var policy = new ApiVersioningPolicy(new WolverineApiVersioningOptions());
        policy.Apply(chains, new GenerationRules(), null!);

        foreach (var chain in chains)
        {
            var endpoint = chain.BuildEndpoint(RouteWarmup.Lazy);
            var apiVersionAttrs = endpoint.Metadata.GetOrderedMetadata<ApiVersionAttribute>();

            apiVersionAttrs.Count.ShouldBe(1);
            apiVersionAttrs[0].Versions.ShouldBe(new[] { chain.ApiVersion! });
        }
    }

    // 10 — duplicate detection: two distinct multi-version handlers overlapping at (GET, /reports, 2.0)
    // throw at the policy stage AFTER expansion.
    [Fact]
    public void overlapping_versions_across_distinct_multi_version_handlers_throw_at_policy()
    {
        var chains = ListOf(
            HttpChain.ChainFor<FirstReportsMultiVersionHandler>(x => x.Get()),
            HttpChain.ChainFor<SecondReportsMultiVersionHandler>(x => x.Get()));

        MultiVersionExpansion.ExpandInPlace(chains);

        // First handler => v1 + v2; second handler => v2 + v3. The shared (GET, /reports, 2.0) is the conflict.
        chains.Count.ShouldBe(4);

        var policy = new ApiVersioningPolicy(new WolverineApiVersioningOptions());
        var ex = Should.Throw<InvalidOperationException>(() => policy.Apply(chains, new GenerationRules(), null!));
        ex.Message.ShouldContain("Duplicate endpoint registration");
        ex.Message.ShouldContain("/reports");
        ex.Message.ShouldContain("2.0");
    }

    // 11 — [MapToApiVersion("X")] producing exactly one version: expansion leaves the chain in place,
    // ApiVersioningPolicy.ResolveAttributes assigns the version (no clone is created).
    [Fact]
    public void mapto_producing_single_version_leaves_chain_in_place_for_policy_resolution()
    {
        var chain = HttpChain.ChainFor<ClassThreeMapToTwoOrdersHandler>(x => x.Get());
        chain.ApiVersion.ShouldBeNull();

        var chains = ListOf(chain);
        MultiVersionExpansion.ExpandInPlace(chains);

        // No clone — same chain reference, ApiVersion still null until policy runs.
        chains.Count.ShouldBe(1);
        ReferenceEquals(chains[0], chain).ShouldBeTrue();
        chains[0].ApiVersion.ShouldBeNull();

        var policy = new ApiVersioningPolicy(new WolverineApiVersioningOptions());
        policy.Apply(chains, new GenerationRules(), null!);

        chains[0].ApiVersion.ShouldBe(new ApiVersion(2, 0));
    }

    // 12 — unversioned handlers in the chain set are untouched by expansion.
    [Fact]
    public void unversioned_chain_is_left_alone_by_expansion()
    {
        var unversioned = HttpChain.ChainFor<UnversionedPlainHandler>(x => x.Get());
        var multi = HttpChain.ChainFor<TwoVersionMethodOrdersHandler>(x => x.Get());

        var chains = ListOf(unversioned, multi);
        MultiVersionExpansion.ExpandInPlace(chains);

        // Multi-version chain expanded to 2; unversioned chain still present, still unversioned.
        chains.Count.ShouldBe(3);
        chains.ShouldContain(unversioned);
        unversioned.ApiVersion.ShouldBeNull();
    }

    // 13 — date-based versions like 2024-01-01 produce a legal identifier in the OperationId suffix.
    // The pre-fix .Replace('.', '_') only handled dotted versions; hyphens leaked through.
    [Fact]
    public void date_based_version_produces_sanitized_operation_id_suffix()
    {
        var chains = ListOf(HttpChain.ChainFor<DateBasedVersionHandler>(x => x.Get()));
        MultiVersionExpansion.ExpandInPlace(chains);

        chains.Count.ShouldBe(2);

        // The version-derived suffix must be a sanitised string (no hyphens, no dots).
        // Inspect only the suffix added by CloneForVersion ("_v..." onwards).
        var suffixes = chains.Select(c => c.OperationId.Substring(c.OperationId.LastIndexOf("_v"))).ToList();
        suffixes.ShouldAllBe(suffix => !suffix.Contains('-') && !suffix.Contains('.'));

        suffixes.ShouldContain("_v2024_01_01");
        suffixes.ShouldContain("_v2025_06_15");
    }
}
