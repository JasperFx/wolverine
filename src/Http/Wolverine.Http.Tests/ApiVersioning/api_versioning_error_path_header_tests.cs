using Alba;
using Shouldly;

namespace Wolverine.Http.Tests.ApiVersioning;

[Collection("integration")]
public class api_versioning_error_path_header_tests : IntegrationContext
{
    public api_versioning_error_path_header_tests(AppFixture fixture) : base(fixture)
    {
    }

    // Versioned chain returning Results.NotFound() — IResult exit path.
    [Fact]
    public async Task headers_emit_on_iresult_not_found()
    {
        var result = await Scenario(x =>
        {
            x.Get.Url("/v1/orders/missing");
            x.StatusCodeShouldBe(404);
        });

        result.Context.Response.Headers.ContainsKey("api-supported-versions").ShouldBeTrue();
        result.Context.Response.Headers["Deprecation"].FirstOrDefault().ShouldNotBeNullOrEmpty();
        result.Context.Response.Headers["Link"].FirstOrDefault().ShouldContain("rel=\"deprecation\"");
    }

    // Versioned chain hitting fluent validation failure — ProblemDetails exit path; codegen short-circuits with return.
    [Fact]
    public async Task headers_emit_on_validation_problem_details()
    {
        var result = await Scenario(x =>
        {
            x.Post.Json(new { Sku = "", Quantity = 0 }).ToUrl("/v1/orders");
            x.StatusCodeShouldBe(400);
        });

        result.Context.Response.Headers.ContainsKey("api-supported-versions").ShouldBeTrue();
        result.Context.Response.Headers["Deprecation"].FirstOrDefault().ShouldNotBeNullOrEmpty();
    }

    // Versioned chain whose Before() middleware short-circuits with 401 — middleware-IResult exit path.
    [Fact]
    public async Task headers_emit_on_middleware_short_circuit_unauthorized()
    {
        var result = await Scenario(x =>
        {
            x.Get.Url("/v1/orders/restricted");
            x.StatusCodeShouldBe(401);
        });

        result.Context.Response.Headers.ContainsKey("api-supported-versions").ShouldBeTrue();
        result.Context.Response.Headers["Deprecation"].FirstOrDefault().ShouldNotBeNullOrEmpty();
    }

    // Sanity: success path on a chain with Before() middleware.
    [Fact]
    public async Task headers_still_emit_on_success_path()
    {
        var result = await Scenario(x =>
        {
            x.WithRequestHeader("X-Test-Auth", "yes");
            x.Get.Url("/v1/orders/restricted");
            x.StatusCodeShouldBeOk();
        });

        result.Context.Response.Headers.ContainsKey("api-supported-versions").ShouldBeTrue();
        result.Context.Response.Headers["Deprecation"].FirstOrDefault().ShouldNotBeNullOrEmpty();
    }
}
