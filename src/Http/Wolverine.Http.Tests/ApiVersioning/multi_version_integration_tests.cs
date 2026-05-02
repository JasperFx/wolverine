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
}
