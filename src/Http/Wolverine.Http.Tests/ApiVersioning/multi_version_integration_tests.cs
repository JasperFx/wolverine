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
    public async Task multi_version_endpoint_v1_is_marked_deprecated()
    {
        var result = await Scenario(x =>
        {
            x.Get.Url("/v1/customers");
            x.StatusCodeShouldBeOk();
        });

        // Per-version deprecation: v1 carries [ApiVersion("1.0", Deprecated=true)].
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
