using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Alba;
using Shouldly;
using WolverineWebApi.ApiVersioning;

namespace Wolverine.Http.Tests.ApiVersioning;

[Collection("integration")]
public class api_versioning_integration_tests : IntegrationContext
{
    public api_versioning_integration_tests(AppFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task v1_orders_returns_v1_response()
    {
        var result = await Scenario(x =>
        {
            x.Get.Url("/v1/orders");
            x.StatusCodeShouldBeOk();
        });

        var response = result.ReadAsJson<OrdersV1Response>();
        response.ShouldNotBeNull();
        response.Orders.ShouldContain("v1-order-1");
        response.Orders.ShouldContain("v1-order-2");
    }

    [Fact]
    public async Task v2_orders_returns_v2_response()
    {
        var result = await Scenario(x =>
        {
            x.Get.Url("/v2/orders");
            x.StatusCodeShouldBeOk();
        });

        var response = result.ReadAsJson<OrdersV2Response>();
        response.ShouldNotBeNull();
        response.Status.ShouldBe("ok");
        response.Items.ShouldContain("v2-a");
    }

    [Fact]
    public async Task v3_orders_emits_sunset_header()
    {
        var result = await Scenario(x =>
        {
            x.Get.Url("/v3/orders");
            x.StatusCodeShouldBeOk();
        });

        var sunsetHeader = result.Context.Response.Headers["Sunset"].FirstOrDefault();
        sunsetHeader.ShouldNotBeNull();

        // RFC 1123 of 2027-01-01T00:00:00Z
        var expectedSunset = new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero)
            .UtcDateTime.ToString("R", CultureInfo.InvariantCulture);
        sunsetHeader.ShouldBe(expectedSunset);
    }

    [Fact]
    public async Task v1_orders_emits_deprecation_header()
    {
        var result = await Scenario(x =>
        {
            x.Get.Url("/v1/orders");
            x.StatusCodeShouldBeOk();
        });

        // Deprecation header must be present (either a date or "true")
        var deprecationHeader = result.Context.Response.Headers["Deprecation"].FirstOrDefault();
        deprecationHeader.ShouldNotBeNull();
        deprecationHeader.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task v1_orders_emits_link_header_for_deprecation()
    {
        var result = await Scenario(x =>
        {
            x.Get.Url("/v1/orders");
            x.StatusCodeShouldBeOk();
        });

        var linkHeader = result.Context.Response.Headers["Link"].FirstOrDefault();
        linkHeader.ShouldNotBeNull();
        linkHeader.ShouldContain("rel=\"deprecation\"");
    }

    [Fact]
    public async Task api_supported_versions_header_lists_all_versions()
    {
        // Any versioned endpoint emits api-supported-versions, built from the per-endpoint
        // ApiVersionMetadata seeded by ApiVersioningPolicy with the full sibling union at the
        // shared (verb, route-after-strip-prefix). For /vN/orders the siblings are v1+v2+v3
        // (OrdersV1Endpoint, OrdersV2Endpoint, OrdersV3PreviewEndpoint).
        var result = await Scenario(x =>
        {
            x.Get.Url("/v2/orders");
            x.StatusCodeShouldBeOk();
        });

        var header = result.Context.Response.Headers["api-supported-versions"].FirstOrDefault();
        header.ShouldNotBeNull();
        header.ShouldBe("1.0, 2.0, 3.0");
    }

    [Fact]
    public async Task unversioned_endpoint_does_not_emit_api_supported_versions()
    {
        // /hello is an unversioned endpoint (PassThrough) — no header writer attached
        var result = await Scenario(x =>
        {
            x.Get.Url("/hello");
            x.StatusCodeShouldBeOk();
        });

        result.Context.Response.Headers.ContainsKey("api-supported-versions").ShouldBeFalse();
    }

    [Fact]
    public async Task swagger_v1_doc_contains_orders_endpoint()
    {
        var result = await Scenario(x =>
        {
            x.Get.Url("/swagger/v1/swagger.json");
            x.StatusCodeShouldBeOk();
        });

        var body = result.ReadAsText();
        body.ShouldContain("/v1/orders");
    }

    [Fact]
    public async Task swagger_default_doc_contains_all_orders_versions()
    {
        var result = await Scenario(x =>
        {
            x.Get.Url("/swagger/default/swagger.json");
            x.StatusCodeShouldBeOk();
        });

        var body = result.ReadAsText();
        body.ShouldContain("/v1/orders");
        body.ShouldContain("/v2/orders");
        body.ShouldContain("/v3/orders");
    }

    [Fact]
    public async Task swagger_v1_doc_marks_orders_deprecated()
    {
        // v1 has a DeprecationPolicy from options, so the operation should be marked deprecated
        var result = await Scenario(x =>
        {
            x.Get.Url("/swagger/v1/swagger.json");
            x.StatusCodeShouldBeOk();
        });

        var body = result.ReadAsText();

        // Parse JSON and navigate to the deprecated property
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        // Navigate to paths./v1/orders.get.deprecated
        var paths = root.GetProperty("paths");
        var v1OrdersPath = paths.GetProperty("/v1/orders");
        var getOperation = v1OrdersPath.GetProperty("get");
        var deprecated = getOperation.GetProperty("deprecated");

        deprecated.GetBoolean().ShouldBeTrue();
    }
}
