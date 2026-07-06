using Asp.Versioning;
using Shouldly;

namespace Wolverine.Http.AspVersioning.Tests;

[ApiVersion("1.0")]
internal class SameRouteV1Handler
{
    [WolverineGet("/same-route")]
    public string Get() => "v1";
}

[ApiVersion("2.0")]
internal class SameRouteV2Handler
{
    [WolverineGet("/same-route")]
    public string Get() => "v2";
}

[ApiVersion("1.0")]
internal class UpperCaseRouteHandler
{
    [WolverineGet("/CaseRoute")]
    public string Get() => "upper";
}

[ApiVersion("2.0")]
internal class LowerCaseRouteHandler
{
    [WolverineGet("/caseroute")]
    public string Get() => "lower";
}

[ApiVersion("1.0")]
internal class LeadingSlashRouteHandler
{
    [WolverineGet("/slash-route")]
    public string Get() => "leading";
}

[ApiVersion("2.0")]
internal class TrailingSlashRouteHandler
{
    [WolverineGet("slash-route/")]
    public string Get() => "trailing";
}

[ApiVersion("1.0")]
internal class DistinctOrdersHandler
{
    [WolverineGet("/distinct-orders")]
    public string Get() => "orders";
}

// Distinct version so the two routes group separately and their version spaces differ observably.
[ApiVersion("2.0")]
internal class DistinctCustomersHandler
{
    [WolverineGet("/distinct-customers")]
    public string Get() => "customers";
}

[ApiVersion("1.0")]
internal class SharedRouteGetHandler
{
    [WolverineGet("/shared-verbs")]
    public string Get() => "get-v1";
}

[ApiVersion("2.0")]
internal class SharedRoutePostHandler
{
    [WolverinePost("/shared-verbs")]
    public string Post() => "post-v2";
}

[ApiVersionNeutral]
internal class NeutralSiblingHandler
{
    [WolverineGet("/neutral-mix")]
    public string Get() => "neutral";
}

[ApiVersion("1.0")]
internal class VersionedSiblingHandler
{
    [WolverineGet("/neutral-mix")]
    public string Get() => "v1";
}

[ApiVersion("1.0")]
internal class RootV1Handler
{
    [WolverineGet("/")]
    public string Get() => "root-v1";
}

[ApiVersion("2.0")]
internal class RootV2Handler
{
    [WolverineGet("/")]
    public string Get() => "root-v2";
}

// URL-segment idiom: two endpoints share one {version:apiVersion} template.
[ApiVersion("1.0")]
internal class SegmentTemplateV1Handler
{
    [WolverineGet("/segment/v{version:apiVersion}/things")]
    public string Get() => "v1";
}

[ApiVersion("2.0")]
internal class SegmentTemplateV2Handler
{
    [WolverineGet("/segment/v{version:apiVersion}/things")]
    public string Get() => "v2";
}

// Multi-method class: both methods share the class-level [ApiVersion]; grouping is still by route.
[ApiVersion("1.0")]
[ApiVersion("2.0")]
internal class MultiMethodSameRouteHandler
{
    [MapToApiVersion("1.0")]
    [WolverineGet("/multi-same")]
    public string Get() => "get";

    [MapToApiVersion("2.0")]
    [WolverinePost("/multi-same")]
    public string Post() => "post";
}

[ApiVersion("1.0")]
[ApiVersion("2.0")]
internal class MultiMethodDistinctRoutesHandler
{
    [MapToApiVersion("1.0")]
    [WolverineGet("/multi-distinct-a")]
    public string A() => "a";

    [MapToApiVersion("2.0")]
    [WolverineGet("/multi-distinct-b")]
    public string B() => "b";
}

/// <summary>
/// Route-only grouping performed by <see cref="AspVersioningPolicy"/>. The finalizer consumes the
/// shared <c>ApiVersionSet</c> into each chain's <see cref="ApiVersionMetadata"/>, so the observable
/// signal of grouping is the aggregate (group-wide) version space: identical across chains in one
/// group, disjoint across separate groups.
/// </summary>
public class GroupingTests : HostlessAspVersioningContext
{
    public GroupingTests(HostlessAspVersioningFixture fixture) : base(fixture) { }

    // Applies the policy over two chains that should land in one group and asserts both see the same
    // aggregate version space. The distinguishing input of each scenario is the handler pair (its routes);
    // this collapses the otherwise-identical assertion shape they share.
    private void AssertSharedVersionSpace(
        HttpChain a,
        HttpChain b,
        params ApiVersion[] union
    )
    {
        VersioningHarness.Apply(a, b);
        a.GroupModel().SupportedApiVersions.ShouldBe(union, ignoreOrder: true);
        b.GroupModel().SupportedApiVersions.ShouldBe(union, ignoreOrder: true);
    }

    private static readonly ApiVersion[] V1AndV2 = [new(1, 0), new(2, 0)];

    [Fact]
    public void same_route_with_distinct_versions_forms_one_group() =>
        AssertSharedVersionSpace(
            VersioningHarness.ChainFor<SameRouteV1Handler>(x => x.Get()),
            VersioningHarness.ChainFor<SameRouteV2Handler>(x => x.Get()),
            V1AndV2
        );

    // Route comparison is OrdinalIgnoreCase.
    [Fact]
    public void routes_differing_only_by_case_form_one_group() =>
        AssertSharedVersionSpace(
            VersioningHarness.ChainFor<UpperCaseRouteHandler>(x => x.Get()),
            VersioningHarness.ChainFor<LowerCaseRouteHandler>(x => x.Get()),
            V1AndV2
        );

    // Leading/trailing slashes are normalized away before grouping.
    [Fact]
    public void routes_differing_only_by_slashes_form_one_group() =>
        AssertSharedVersionSpace(
            VersioningHarness.ChainFor<LeadingSlashRouteHandler>(x => x.Get()),
            VersioningHarness.ChainFor<TrailingSlashRouteHandler>(x => x.Get()),
            V1AndV2
        );

    [Fact]
    public void distinct_routes_form_distinct_groups()
    {
        var orders = VersioningHarness.ChainFor<DistinctOrdersHandler>(x => x.Get());
        var customers = VersioningHarness.ChainFor<DistinctCustomersHandler>(x => x.Get());

        VersioningHarness.Apply(orders, customers);

        orders.GroupModel().SupportedApiVersions.ShouldBe(new[] { new ApiVersion(1, 0) });
        customers.GroupModel().SupportedApiVersions.ShouldBe(new[] { new ApiVersion(2, 0) });
    }

    // The HTTP verb is deliberately not part of the grouping key, so GET + POST at one route share a
    // version space. This cross-verb over-advertisement is the accepted tradeoff of route-only grouping.
    [Fact]
    public void same_route_with_different_verbs_forms_one_group() =>
        AssertSharedVersionSpace(
            VersioningHarness.ChainFor<SharedRouteGetHandler>(x => x.Get()),
            VersioningHarness.ChainFor<SharedRoutePostHandler>(x => x.Post()),
            V1AndV2
        );

    [Fact]
    public void neutral_chain_does_not_contribute_to_versioned_group()
    {
        var neutral = VersioningHarness.ChainFor<NeutralSiblingHandler>(x => x.Get());
        var versioned = VersioningHarness.ChainFor<VersionedSiblingHandler>(x => x.Get());

        VersioningHarness.Apply(neutral, versioned);

        versioned.GroupModel().SupportedApiVersions.ShouldBe(new[] { new ApiVersion(1, 0) });
        neutral.VersionMetadata()!.IsApiVersionNeutral.ShouldBeTrue();
    }

    // "/" normalizes to the empty grouping key — one logical root route, one group.
    [Fact]
    public void root_route_endpoints_form_one_group() =>
        AssertSharedVersionSpace(
            VersioningHarness.ChainFor<RootV1Handler>(x => x.Get()),
            VersioningHarness.ChainFor<RootV2Handler>(x => x.Get()),
            V1AndV2
        );

    // URL-segment versioning: the version is a {version:apiVersion} route parameter, not template text,
    // so both siblings normalize to the same grouping key. The bridge never rewrites routes.
    [Fact]
    public void shared_url_segment_template_forms_one_group() =>
        AssertSharedVersionSpace(
            VersioningHarness.ChainFor<SegmentTemplateV1Handler>(x => x.Get()),
            VersioningHarness.ChainFor<SegmentTemplateV2Handler>(x => x.Get()),
            V1AndV2
        );

    [Fact]
    public void same_route_methods_on_one_class_form_one_group() =>
        AssertSharedVersionSpace(
            VersioningHarness.ChainFor<MultiMethodSameRouteHandler>(x => x.Get()),
            VersioningHarness.ChainFor<MultiMethodSameRouteHandler>(x => x.Post()),
            V1AndV2
        );

    // Grouping is by route, not declaring type: methods at different routes form separate groups.
    [Fact]
    public void different_route_methods_on_one_class_form_separate_groups()
    {
        var a = VersioningHarness.ChainFor<MultiMethodDistinctRoutesHandler>(x => x.A());
        var b = VersioningHarness.ChainFor<MultiMethodDistinctRoutesHandler>(x => x.B());

        VersioningHarness.Apply(a, b);

        a.GroupModel().SupportedApiVersions.ShouldBe(new[] { new ApiVersion(1, 0) });
        b.GroupModel().SupportedApiVersions.ShouldBe(new[] { new ApiVersion(2, 0) });
    }
}
