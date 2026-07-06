using System.Text.Json;
using Alba;
using Asp.Versioning;
using Asp.Versioning.ApiExplorer;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Shouldly;

namespace Wolverine.Http.AspVersioning.Tests.Parity;

/// <summary>
/// OpenAPI / ApiExplorer parity: for each logical resource a vanilla Asp.Versioning minimal-API endpoint
/// (<c>/native/*</c>) and its Wolverine twin (<c>/wolverine/*</c>) are registered against the same host,
/// and each test asserts the version-partitioning facts the bridge determines — which paths land in which
/// per-version document, deprecation flags, path substitution — are identical between the two. The package
/// adds no OpenAPI-specific code: the attached <see cref="ApiVersionMetadata"/> flows through Wolverine's
/// ApiExplorer descriptions into Asp.Versioning's <c>VersionedApiDescriptionProvider</c>.
///
/// Assertions are route-scoped (filter <c>ApiDescription</c> by <c>RelativePath</c>) so they are unaffected
/// by the assembly-wide version space every discovered endpoint shares.
/// </summary>
public class OpenApiParityTests : AspVersioningIntegrationContext
{
    public OpenApiParityTests(AspVersioningAppFixture fixture)
        : base(fixture) { }

    // The set of per-version document group names in which a route (by its ApiExplorer RelativePath, no
    // leading slash) appears.
    private HashSet<string?> GroupsFor(string relativePath) =>
        Service<IApiDescriptionGroupCollectionProvider>()
            .ApiDescriptionGroups.Items.Where(g =>
                g.Items.Any(d => d.RelativePath == relativePath && d.HttpMethod == "GET")
            )
            .Select(g => g.GroupName)
            .ToHashSet();

    private bool RouteInGroup(string relativePath, string? groupName) =>
        GroupsFor(relativePath).Contains(groupName);

    // The described parameter names for a route within a given version's group.
    private List<string> ParametersFor(string relativePath, ApiVersion version)
    {
        var groupName = GroupNameFor(version);
        return Service<IApiDescriptionGroupCollectionProvider>()
            .ApiDescriptionGroups.Items.Single(g => g.GroupName == groupName)
            .Items.First(d => d.RelativePath == relativePath && d.HttpMethod == "GET")
            .ParameterDescriptions.Select(p => p.Name)
            .ToList();
    }

    // The "get" operation JsonElement for a route in a given version's rendered Swashbuckle document. The
    // JsonDocument is intentionally not disposed: the returned JsonElement roots it, and disposing would
    // invalidate the element.
    private async Task<JsonElement> GetOperation(ApiVersion version, string path)
    {
        var groupName = GroupNameFor(version);
        var result = await Scenario(x =>
        {
            x.Get.Url($"/swagger/{groupName}/swagger.json");
            x.StatusCodeShouldBeOk();
        });

        var doc = JsonDocument.Parse(await result.ReadAsTextAsync());
        return doc.RootElement.GetProperty("paths").GetProperty(path).GetProperty("get");
    }

    // Versioned Wolverine endpoints reach the versioned ApiExplorer and partition identically to native:
    // the orders twins appear in the same set of per-version document groups, which is exactly {v1, v2}.
    [Fact]
    public void openapi_version_groups_match_native()
    {
        var nativeGroups = GroupsFor("native/orders");
        var wolverineGroups = GroupsFor("wolverine/orders");

        nativeGroups.ShouldNotBeEmpty();
        wolverineGroups.ShouldBe(nativeGroups, ignoreOrder: true);

        // Completeness: the orders twins live in exactly their resolved version groups {v1, v2}.
        var expected = new HashSet<string?>
        {
            GroupNameFor(new ApiVersion(1, 0)),
            GroupNameFor(new ApiVersion(2, 0)),
        };
        nativeGroups.ShouldBe(expected, ignoreOrder: true);
    }

    // Per-version exclusivity: an endpoint mapped only to v1 appears in the v1 group and NOT the v2 group,
    // identically for the native and Wolverine twins.
    [Fact]
    public void v1only_group_membership_matches_native()
    {
        var v1 = GroupNameFor(new ApiVersion(1, 0));
        var v2 = GroupNameFor(new ApiVersion(2, 0));

        GroupsFor("wolverine/v1only").ShouldBe(GroupsFor("native/v1only"), ignoreOrder: true);

        GroupsFor("native/v1only").ShouldContain(v1);
        GroupsFor("native/v1only").ShouldNotContain(v2);
        GroupsFor("wolverine/v1only").ShouldContain(v1);
        GroupsFor("wolverine/v1only").ShouldNotContain(v2);
    }

    // A version-neutral endpoint appears in EVERY version's document group, identically for both twins.
    [Fact]
    public void neutral_endpoint_in_every_group_matches_native()
    {
        var provider = Service<IApiVersionDescriptionProvider>();

        foreach (var description in provider.ApiVersionDescriptions)
        {
            RouteInGroup("native/health", description.GroupName)
                .ShouldBeTrue(
                    $"native neutral endpoint missing from document '{description.GroupName}'"
                );
            RouteInGroup("wolverine/health", description.GroupName)
                .ShouldBeTrue(
                    $"wolverine neutral endpoint missing from document '{description.GroupName}'"
                );
        }

        // And the twins occupy exactly the same groups.
        GroupsFor("wolverine/health").ShouldBe(GroupsFor("native/health"), ignoreOrder: true);
    }

    // A deprecated version still produces a document and its operation is flagged deprecated, identically
    // on both twins; a supported version's operation is not flagged. Uses the uniquely-deprecated 10.0
    // twins and the supported 2.0 orders twins. (Swashbuckle only emits `deprecated` when true.)
    [Fact]
    public async Task deprecated_operation_marked_matches_native()
    {
        Service<IApiVersionDescriptionProvider>()
            .ApiVersionDescriptions.Single(d => d.ApiVersion == new ApiVersion(10, 0))
            .IsDeprecated.ShouldBeTrue();

        var nativeDeprecated = await GetOperation(new ApiVersion(10, 0), "/native/deprecated");
        var wolverineDeprecated = await GetOperation(
            new ApiVersion(10, 0),
            "/wolverine/deprecated"
        );
        nativeDeprecated.GetProperty("deprecated").GetBoolean().ShouldBeTrue();
        wolverineDeprecated.GetProperty("deprecated").GetBoolean().ShouldBeTrue();

        var nativeCurrent = await GetOperation(new ApiVersion(2, 0), "/native/orders");
        var wolverineCurrent = await GetOperation(new ApiVersion(2, 0), "/wolverine/orders");
        nativeCurrent.TryGetProperty("deprecated", out _).ShouldBeFalse();
        wolverineCurrent.TryGetProperty("deprecated", out _).ShouldBeFalse();
    }

    // The supported-wins version (8.0) folds to supported, so its version description is NOT deprecated and
    // its document operation is not marked deprecated — the same fold the header parity proves, verified in
    // the document, on both twins.
    [Fact]
    public async Task supported_wins_document_not_deprecated_matches_native()
    {
        Service<IApiVersionDescriptionProvider>()
            .ApiVersionDescriptions.Single(d => d.ApiVersion == new ApiVersion(8, 0))
            .IsDeprecated.ShouldBeFalse();

        var native = await GetOperation(new ApiVersion(8, 0), "/native/conflict");
        var wolverine = await GetOperation(new ApiVersion(8, 0), "/wolverine/conflict");
        native.TryGetProperty("deprecated", out _).ShouldBeFalse();
        wolverine.TryGetProperty("deprecated", out _).ShouldBeFalse();
    }

    // An advertised-only version (implemented by no endpoint) yields NO version document: 3.9 is advertised
    // by the advertised twins but served by no one, so Asp.Versioning's ApiExplorer bucketizer drops it.
    // Both twins appear only in the v3 group they actually implement.
    [Fact]
    public void advertised_only_version_yields_no_document_matches_native()
    {
        Service<IApiVersionDescriptionProvider>()
            .ApiVersionDescriptions.Select(d => d.ApiVersion)
            .ShouldNotContain(new ApiVersion(3, 9));

        var v3 = GroupNameFor(new ApiVersion(3, 0));
        GroupsFor("wolverine/advertised")
            .ShouldBe(GroupsFor("native/advertised"), ignoreOrder: true);
        GroupsFor("native/advertised").ShouldBe(new HashSet<string?> { v3 }, ignoreOrder: true);
    }

    // The bridge does no native-style route rewriting: a query-versioned route keeps its literal path in the
    // document (no auto "/v1/" prefix), identically for both twins.
    [Fact]
    public async Task query_versioned_route_path_is_literal_matches_native()
    {
        var groupName = GroupNameFor(new ApiVersion(1, 0));
        var result = await Scenario(x =>
        {
            x.Get.Url($"/swagger/{groupName}/swagger.json");
            x.StatusCodeShouldBeOk();
        });

        using var doc = JsonDocument.Parse(await result.ReadAsTextAsync());
        var paths = doc.RootElement.GetProperty("paths");

        paths.TryGetProperty("/native/orders", out _).ShouldBeTrue();
        paths.TryGetProperty("/wolverine/orders", out _).ShouldBeTrue();
        paths.TryGetProperty("/v1/native/orders", out _).ShouldBeFalse();
        paths.TryGetProperty("/v1/wolverine/orders", out _).ShouldBeFalse();
    }

    // The version reader's parameter ("api-version", the default query-string reader) is described on the
    // versioned operation for both twins.
    [Fact]
    public void versioned_operation_advertises_api_version_parameter_matches_native()
    {
        ParametersFor("native/orders", new ApiVersion(1, 0)).ShouldContain("api-version");
        ParametersFor("wolverine/orders", new ApiVersion(1, 0)).ShouldContain("api-version");
    }

    // End-to-end through Swashbuckle: each version document renders and contains BOTH twins' shared orders
    // route — the attached metadata flows all the way to a concrete document, identically for both.
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task rendered_document_contains_both_twins(int major)
    {
        var groupName = GroupNameFor(new ApiVersion(major, 0));
        var result = await Scenario(x =>
        {
            x.Get.Url($"/swagger/{groupName}/swagger.json");
            x.StatusCodeShouldBeOk();
        });

        var doc = await result.ReadAsTextAsync();
        doc.ShouldContain("/native/orders");
        doc.ShouldContain("/wolverine/orders");
    }
}
