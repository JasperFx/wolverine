using Asp.Versioning;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Interfaces;
using Microsoft.OpenApi.Models;
using Shouldly;
using Wolverine.Http.ApiVersioning;
using WolverineWebApi.ApiVersioning;

namespace Wolverine.Http.Tests.ApiVersioning;

public class WolverineApiVersioningSwaggerOperationFilterTests
{
    private static OpenApiOperation MakeOperation() => new() { Summary = "Test" };

    [Fact]
    public void filter_marks_operation_deprecated_when_state_has_deprecation_policy()
    {
        var state = new ApiVersionEndpointHeaderState(
            new ApiVersion(1, 0),
            Sunset: null,
            Deprecation: new DeprecationPolicy());
        var metadata = new List<object> { state };
        var operation = MakeOperation();

        WolverineApiVersioningSwaggerOperationFilter.ApplyFromMetadata(operation, metadata);

        operation.Deprecated.ShouldBeTrue();
    }

    [Fact]
    public void filter_does_nothing_when_no_state_metadata()
    {
        var metadata = new List<object>();
        var operation = MakeOperation();

        WolverineApiVersioningSwaggerOperationFilter.ApplyFromMetadata(operation, metadata);

        operation.Deprecated.ShouldBeFalse();
        ((IDictionary<string, IOpenApiExtension>)operation.Extensions).ShouldNotContainKey("x-api-versioning");
    }

    [Fact]
    public void filter_emits_x_api_versioning_with_sunset_date()
    {
        var sunsetDate = new DateTimeOffset(2027, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var sunsetPolicy = new SunsetPolicy(sunsetDate);
        var state = new ApiVersionEndpointHeaderState(
            new ApiVersion(1, 0),
            Sunset: sunsetPolicy,
            Deprecation: new DeprecationPolicy());
        var metadata = new List<object> { state };
        var operation = MakeOperation();

        WolverineApiVersioningSwaggerOperationFilter.ApplyFromMetadata(operation, metadata);

        var extensions = (IDictionary<string, IOpenApiExtension>)operation.Extensions;
        extensions.ShouldContainKey("x-api-versioning");
        var ext = (OpenApiObject)extensions["x-api-versioning"];
        ((IDictionary<string, IOpenApiAny>)ext).ShouldContainKey("sunset");
        var sunsetStr = ((OpenApiString)ext["sunset"]).Value;
        sunsetStr.ShouldBe(sunsetDate.UtcDateTime.ToString("R"));
    }

    [Fact]
    public void filter_emits_links_array_in_x_api_versioning()
    {
        var linkUri = new Uri("https://example.com/sunset");
        var sunsetPolicy = new SunsetPolicy(new LinkHeaderValue(linkUri, "sunset"));
        var state = new ApiVersionEndpointHeaderState(
            new ApiVersion(1, 0),
            Sunset: sunsetPolicy,
            Deprecation: null);
        var metadata = new List<object> { state };
        var operation = MakeOperation();

        WolverineApiVersioningSwaggerOperationFilter.ApplyFromMetadata(operation, metadata);

        var extensions = (IDictionary<string, IOpenApiExtension>)operation.Extensions;
        extensions.ShouldContainKey("x-api-versioning");
        var ext = (OpenApiObject)extensions["x-api-versioning"];
        ((IDictionary<string, IOpenApiAny>)ext).ShouldContainKey("links");
        var links = (OpenApiArray)ext["links"];
        links.Count.ShouldBe(1);
        var firstLink = (OpenApiObject)links[0];
        ((IDictionary<string, IOpenApiAny>)firstLink).ShouldContainKey("href");
        ((OpenApiString)firstLink["href"]).Value.ShouldBe(linkUri.ToString());
    }
}
