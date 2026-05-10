using Alba;
using Shouldly;

namespace Wolverine.Http.Tests.ApiVersioning;

[Collection("integration")]
public class api_versioning_error_path_header_tests : IntegrationContext
{
    // Per-endpoint sibling union: these error-path endpoints only declare v1 and have no v3
    // sibling at the same (verb, route), so api-supported-versions correctly reports "1.0" only.
    private const string ExpectedSupportedVersions = "1.0";

    public api_versioning_error_path_header_tests(AppFixture fixture) : base(fixture)
    {
    }

    // Versioned chain returning Results.NotFound() - IResult exit path.
    [Fact]
    public async Task headers_emit_on_iresult_not_found()
    {
        var result = await Scenario(x =>
        {
            x.Get.Url("/v1/orders/missing");
            x.StatusCodeShouldBe(404);
        });

        // The header must list both versions discovered from sunset/deprecation policies in Program.cs:
        // Deprecate("1.0") and Sunset("3.0"), sorted ascending.
        result.Context.Response.Headers["api-supported-versions"].ToString().ShouldBe(ExpectedSupportedVersions);
        result.Context.Response.Headers["Deprecation"].FirstOrDefault().ShouldNotBeNullOrEmpty();
        result.Context.Response.Headers["Link"].First()!.ShouldContain("rel=\"deprecation\"");
    }

    // Versioned chain hitting fluent validation failure - ProblemDetails exit path; codegen short-circuits with return.
    [Fact]
    public async Task headers_emit_on_validation_problem_details()
    {
        var result = await Scenario(x =>
        {
            x.Post.Json(new { Sku = "", Quantity = 0 }).ToUrl("/v1/orders");
            x.StatusCodeShouldBe(400);
        });

        result.Context.Response.Headers["api-supported-versions"].ToString().ShouldBe(ExpectedSupportedVersions);
        result.Context.Response.Headers["Deprecation"].FirstOrDefault().ShouldNotBeNullOrEmpty();
    }

    // Versioned chain whose Before() middleware short-circuits with 401 - middleware-IResult exit path.
    [Fact]
    public async Task headers_emit_on_middleware_short_circuit_unauthorized()
    {
        var result = await Scenario(x =>
        {
            x.Get.Url("/v1/orders/restricted");
            x.StatusCodeShouldBe(401);
        });

        result.Context.Response.Headers["api-supported-versions"].ToString().ShouldBe(ExpectedSupportedVersions);
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

        result.Context.Response.Headers["api-supported-versions"].ToString().ShouldBe(ExpectedSupportedVersions);
        result.Context.Response.Headers["Deprecation"].FirstOrDefault().ShouldNotBeNullOrEmpty();
    }

    // Regression: 5xx responses produced by the global ASP.NET Core exception handler bypass
    // the chain's middleware pipeline and therefore must NOT receive versioning headers.
    // This is a documented out-of-scope (see docs/guide/http/versioning.md). UseExceptionHandler
    // is scoped to /v1/orders/throws inside Program.cs so other tests are unaffected.
    [Fact]
    public async Task headers_do_not_emit_on_global_exception_handler_response()
    {
        var result = await Scenario(x =>
        {
            x.Get.Url("/v1/orders/throws");
            x.StatusCodeShouldBe(500);
        });

        // Body marker proves the response really came from the scoped UseExceptionHandler in
        // Program.cs (not from Wolverine's own ProblemDetails OnException middleware). Without
        // this, a future change that lets Wolverine itself answer the throws endpoint with a 500
        // would silently turn the absent-headers assertion into a tautology.
        (await result.ReadAsTextAsync()).ShouldContain("global-exception-handler");

        result.Context.Response.Headers.ContainsKey("api-supported-versions").ShouldBeFalse();
        result.Context.Response.Headers.ContainsKey("Deprecation").ShouldBeFalse();
        result.Context.Response.Headers.ContainsKey("Sunset").ShouldBeFalse();
        result.Context.Response.Headers.ContainsKey("Link").ShouldBeFalse();
    }
}
