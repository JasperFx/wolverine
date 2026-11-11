using Asp.Versioning;
using Microsoft.OpenApi;
using System.Text.Json.Nodes;
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
        (operation.Extensions?.ContainsKey("x-api-versioning") ?? false).ShouldBeFalse();
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

        var extensions = operation.Extensions.ShouldNotBeNull();
        extensions.ShouldContainKey("x-api-versioning");
        var ext = extensions["x-api-versioning"].ShouldBeOfType<JsonNodeExtension>().Node.AsObject();
        ext.ContainsKey("sunset").ShouldBeTrue();
        var sunsetStr = ext["sunset"]!.GetValue<string>();
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

        var extensions = operation.Extensions.ShouldNotBeNull();
        extensions.ShouldContainKey("x-api-versioning");
        var ext = extensions["x-api-versioning"].ShouldBeOfType<JsonNodeExtension>().Node.AsObject();
        ext.ContainsKey("links").ShouldBeTrue();
        var links = ext["links"]!.AsArray();
        links.Count.ShouldBe(1);
        var firstLink = links[0]!.AsObject();
        firstLink.ContainsKey("href").ShouldBeTrue();
        firstLink["href"]!.GetValue<string>().ShouldBe(linkUri.ToString());
    }
}
