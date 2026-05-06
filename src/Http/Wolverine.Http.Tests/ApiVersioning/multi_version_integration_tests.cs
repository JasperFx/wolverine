using Alba;
using Shouldly;
using WolverineWebApi.ApiVersioning;

namespace Wolverine.Http.Tests.ApiVersioning;

[Collection("integration")]
public class multi_version_integration_tests : IntegrationContext
{
    public multi_version_integration_tests(AppFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task multi_version_endpoint_registers_v1_route()
    {
        var result = await Scenario(x =>
        {
            x.Get.Url("/v1/customers");
            x.StatusCodeShouldBeOk();
        });

        var response = result.ReadAsJson<CustomersResponse>();
        response!.Names.ShouldContain("alice");
    }

    [Fact]
    public async Task multi_version_endpoint_registers_v2_route()
    {
        var result = await Scenario(x =>
        {
            x.Get.Url("/v2/customers");
            x.StatusCodeShouldBeOk();
        });

        var response = result.ReadAsJson<CustomersResponse>();
        response!.Names.ShouldContain("alice");
    }

    [Fact]
    public async Task multi_version_endpoint_registers_v3_route()
    {
        var result = await Scenario(x =>
        {
            x.Get.Url("/v3/customers");
            x.StatusCodeShouldBeOk();
        });

        var response = result.ReadAsJson<CustomersResponse>();
        response!.Names.ShouldContain("alice");
    }

    [Fact]
    public async Task multi_version_endpoint_v1_carries_globally_configured_deprecation()
    {
        var result = await Scenario(x =>
        {
            x.Get.Url("/v1/customers");
            x.StatusCodeShouldBeOk();
        });

        // Program.cs registers options.Deprecate("1.0") globally, so v1 endpoints emit the
        // header regardless of the per-version [ApiVersion(..., Deprecated = true)] attribute.
        // This test pins down the options-driven path. Attribute-only deprecation is asserted
        // separately on /v4/customers, which has no matching options.Deprecate call.
        var deprecation = result.Context.Response.Headers["Deprecation"].FirstOrDefault();
        deprecation.ShouldNotBeNull();
    }

    [Fact]
    public async Task multi_version_endpoint_v2_is_not_deprecated()
    {
        var result = await Scenario(x =>
        {
            x.Get.Url("/v2/customers");
            x.StatusCodeShouldBeOk();
        });

        // v2 has [ApiVersion("2.0")] without Deprecated; no per-version Deprecation header.
        var deprecation = result.Context.Response.Headers["Deprecation"].FirstOrDefault();
        deprecation.ShouldBeNull();
    }

    [Fact]
    public async Task v4_endpoint_deprecation_comes_from_attribute_alone()
    {
        var result = await Scenario(x =>
        {
            x.Get.Url("/v4/customers");
            x.StatusCodeShouldBeOk();
        });

        // CustomersV4AttributeDeprecatedEndpoint is decorated with [ApiVersion("4.0", Deprecated = true)]
        // and Program.cs registers no options.Deprecate("4.0"). The Deprecation header therefore
        // proves the attribute-driven deprecation path works independently of the options map.
        var deprecation = result.Context.Response.Headers["Deprecation"].FirstOrDefault();
        deprecation.ShouldNotBeNull();
    }

    [Fact]
    public async Task mapto_apiversion_registers_only_listed_version()
    {
        var v2 = await Scenario(x =>
        {
            x.Get.Url("/v2/customers/v2-only");
            x.StatusCodeShouldBeOk();
        });

        var response = v2.ReadAsJson<CustomersResponse>();
        response!.Names.ShouldContain("v2-only-alice");

        // v1 and v3 routes for this endpoint must not exist.
        await Scenario(x =>
        {
            x.Get.Url("/v1/customers/v2-only");
            x.StatusCodeShouldBe(404);
        });

        await Scenario(x =>
        {
            x.Get.Url("/v3/customers/v2-only");
            x.StatusCodeShouldBe(404);
        });
    }

    [Fact]
    public async Task multi_version_endpoint_emits_api_supported_versions_with_sibling_set()
    {
        // GET /v1/customers is one clone of CustomersMultiVersionEndpoint, which declares 1.0/2.0/3.0,
        // and the same (verb, route-after-strip-prefix) is also served by CustomersV4AttributeDeprecatedEndpoint
        // at v4.0. The api-supported-versions header on this clone must report the FULL sibling union
        // (every version that serves GET /customers regardless of which handler class), not just the
        // per-options policy keys nor the per-clone version. This pins the per-endpoint metadata wiring.
        var result = await Scenario(x =>
        {
            x.Get.Url("/v1/customers");
            x.StatusCodeShouldBeOk();
        });

        var supported = result.Context.Response.Headers["api-supported-versions"].FirstOrDefault();
        supported.ShouldNotBeNull();

        var values = supported!
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .OrderBy(v => v)
            .ToArray();

        // v1 is deprecated (per-attribute) so reported under deprecated, not supported. v2/v3 supported
        // come from CustomersMultiVersionEndpoint, v4 is deprecated (per-attribute) so reported under
        // deprecated. The combined api-supported-versions header reports the full sibling chain, so
        // every version that has a clone at GET /customers must appear.
        values.ShouldContain("1.0");
        values.ShouldContain("2.0");
        values.ShouldContain("3.0");
        values.ShouldContain("4.0");
    }
}
