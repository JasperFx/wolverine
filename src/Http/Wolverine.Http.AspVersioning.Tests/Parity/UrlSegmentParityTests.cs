using Alba;
using Asp.Versioning;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Wolverine.Http.AspVersioning.Tests.Parity;

// ---------------------------------------------------------------------------------------------------
// URL-segment parity. Asp.Versioning does NOT rewrite routes — URL-segment versioning is the developer
// authoring a {version:apiVersion} route parameter plus the `apiVersion` constraint that AddApiVersioning
// registers. The bridge supports it exactly like a vanilla minimal API: the user writes the segment,
// sibling versions share one template, and UrlSegmentApiVersionReader selects by the requested segment.
// This host proves a Wolverine endpoint at "/wolverine/v{version:apiVersion}/things" behaves like its
// native twin.
// ---------------------------------------------------------------------------------------------------

public class UrlSegmentParityFixture : ParityFixture
{
    public override Task InitializeAsync() =>
        BuildHost(
            services =>
            {
                services
                    .AddApiVersioning(options =>
                    {
                        options.ReportApiVersions = true;
                        options.ApiVersionReader = new UrlSegmentApiVersionReader();
                    })
                    .AddApiExplorer(options =>
                    {
                        options.GroupNameFormat = "'v'VVV";
                        // Substitute the concrete version into described paths: "/native/v1/things", not "/v{version}/things".
                        options.SubstituteApiVersionInUrl = true;
                    });

                services.AddEndpointsApiExplorer();
            },
            // The version lives in a {version:apiVersion} route parameter, so both siblings share one template.
            app =>
                ParityEndpoints.MapTwoVersionRoute(
                    app,
                    "/native/v{version:apiVersion}/things",
                    "things-v1",
                    "things-v2"
                )
        );
}

[CollectionDefinition("urlsegment")]
public class UrlSegmentParityCollection : ICollectionFixture<UrlSegmentParityFixture>;

// Wolverine URL-segment twins: two classes sharing one {version:apiVersion} template, each declaring its
// own version. The `version` route parameter is intentionally NOT a handler argument.
public class WolverineUrlSegmentV1ParityEndpoint
{
    [WolverineGet("/wolverine/v{version:apiVersion}/things")]
    [ApiVersion("1.0")]
    public string Get() => "things-v1";
}

public class WolverineUrlSegmentV2ParityEndpoint
{
    [WolverineGet("/wolverine/v{version:apiVersion}/things")]
    [ApiVersion("2.0")]
    public string Get() => "things-v2";
}

[Collection("urlsegment")]
public class UrlSegmentParityTests
{
    private readonly IAlbaHost _host;

    public UrlSegmentParityTests(UrlSegmentParityFixture fixture) => _host = fixture.Host;

    // The version in the URL segment routes to the matching handler, identically on both twins.
    [Theory]
    [InlineData(1, "things-v1")]
    [InlineData(2, "things-v2")]
    public async Task url_segment_routes_and_body_match_native(int major, string expected)
    {
        var native = await _host.Scenario(x =>
        {
            x.Get.Url($"/native/v{major}/things");
            x.StatusCodeShouldBeOk();
        });
        (await native.ReadAsTextAsync()).Trim().ShouldBe(expected);

        var wolverine = await _host.Scenario(x =>
        {
            x.Get.Url($"/wolverine/v{major}/things");
            x.StatusCodeShouldBeOk();
        });
        (await wolverine.ReadAsTextAsync()).Trim().ShouldBe(expected);
    }

    // ApiExplorer substitutes the concrete version into the described path for both twins
    // (SubstituteApiVersionInUrl = true); no unsubstituted "{version}" token leaks into either.
    [Fact]
    public void openapi_substitutes_concrete_version_for_both_twins()
    {
        var paths = _host
            .Services.GetRequiredService<IApiDescriptionGroupCollectionProvider>()
            .ApiDescriptionGroups.Items.SelectMany(g => g.Items)
            .Where(d => d.HttpMethod == "GET")
            .Select(d => d.RelativePath)
            .ToList();

        paths.ShouldContain("native/v1/things");
        paths.ShouldContain("native/v2/things");
        paths.ShouldContain("wolverine/v1/things");
        paths.ShouldContain("wolverine/v2/things");
        paths.ShouldNotContain(p => p != null && p.Contains("{version}"));
    }
}
