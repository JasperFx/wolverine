using System.Globalization;
using Asp.Versioning;
using Microsoft.AspNetCore.Http;
using Shouldly;
using Wolverine.Http.ApiVersioning;

namespace Wolverine.Http.Tests.ApiVersioning;

public class ApiVersionHeaderWriterTests
{
    // Helper: build a DefaultHttpContext that has the given state attached as endpoint metadata.
    private static DefaultHttpContext ContextWithState(ApiVersionEndpointHeaderState state)
    {
        var ctx = new DefaultHttpContext();
        var endpoint = new Endpoint(
            _ => Task.CompletedTask,
            new EndpointMetadataCollection(state),
            "test");
        ctx.SetEndpoint(endpoint);
        return ctx;
    }

    // Helper: build a DefaultHttpContext with NO endpoint state.
    private static DefaultHttpContext ContextWithNoState()
    {
        return new DefaultHttpContext();
    }

    // 1 — no state → no headers emitted
    [Fact]
    public async Task no_state_metadata_emits_no_headers()
    {
        var opts = new WolverineApiVersioningOptions();
        var writer = new ApiVersionHeaderWriter(opts);
        var ctx = ContextWithNoState();

        await writer.WriteAsync(ctx);

        ctx.Response.Headers.ContainsKey("api-supported-versions").ShouldBeFalse();
        ctx.Response.Headers.ContainsKey("Deprecation").ShouldBeFalse();
        ctx.Response.Headers.ContainsKey("Sunset").ShouldBeFalse();
        ctx.Response.Headers.ContainsKey("Link").ShouldBeFalse();
    }

    // 2 — api-supported-versions is union of sunset + deprecation policy keys, sorted ascending
    [Fact]
    public async Task api_supported_versions_includes_sunset_and_deprecation_keys()
    {
        var date1 = new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var date2 = new DateTimeOffset(2028, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var opts = new WolverineApiVersioningOptions();
        opts.Sunset("1.0").On(date1);
        opts.Deprecate("2.0").On(date2);

        var writer = new ApiVersionHeaderWriter(opts);
        var state = new ApiVersionEndpointHeaderState(
            new ApiVersion(1, 0),
            opts.SunsetPolicies[new ApiVersion(1, 0)],
            null);
        var ctx = ContextWithState(state);

        await writer.WriteAsync(ctx);

        ctx.Response.Headers["api-supported-versions"].ToString().ShouldBe("1.0, 2.0");
    }

    // 3 — Deprecation header uses IMF-fixdate when policy has a date
    [Fact]
    public async Task deprecation_with_date_uses_imf_fixdate()
    {
        var depDate = new DateTimeOffset(2030, 6, 15, 0, 0, 0, TimeSpan.Zero);
        var opts = new WolverineApiVersioningOptions();
        var depPolicy = new DeprecationPolicy(depDate);
        var state = new ApiVersionEndpointHeaderState(new ApiVersion(1, 0), null, depPolicy);
        var writer = new ApiVersionHeaderWriter(opts);
        var ctx = ContextWithState(state);

        await writer.WriteAsync(ctx);

        var expected = depDate.UtcDateTime.ToString("R", CultureInfo.InvariantCulture);
        ctx.Response.Headers["Deprecation"].ToString().ShouldBe(expected);
    }

    // 4 — Deprecation header is "true" when policy has no date
    [Fact]
    public async Task deprecation_without_date_emits_true_token()
    {
        var opts = new WolverineApiVersioningOptions();
        var depPolicy = new DeprecationPolicy();
        var state = new ApiVersionEndpointHeaderState(new ApiVersion(1, 0), null, depPolicy);
        var writer = new ApiVersionHeaderWriter(opts);
        var ctx = ContextWithState(state);

        await writer.WriteAsync(ctx);

        ctx.Response.Headers["Deprecation"].ToString().ShouldBe("true");
    }

    // 5 — Sunset header uses IMF-fixdate
    [Fact]
    public async Task sunset_emits_imf_fixdate()
    {
        var sunsetDate = new DateTimeOffset(2029, 12, 31, 0, 0, 0, TimeSpan.Zero);
        var opts = new WolverineApiVersioningOptions();
        var sunsetPolicy = new SunsetPolicy(sunsetDate);
        var state = new ApiVersionEndpointHeaderState(new ApiVersion(1, 0), sunsetPolicy, null);
        var writer = new ApiVersionHeaderWriter(opts);
        var ctx = ContextWithState(state);

        await writer.WriteAsync(ctx);

        var expected = sunsetDate.UtcDateTime.ToString("R", CultureInfo.InvariantCulture);
        ctx.Response.Headers["Sunset"].ToString().ShouldBe(expected);
    }

    // 6 — Link header: single sunset link with rel="sunset", title, type
    [Fact]
    public async Task single_link_with_sunset_rel()
    {
        var sunsetDate = new DateTimeOffset(2029, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var linkUri = new Uri("https://example.com/info");
        var link = new LinkHeaderValue(linkUri, "sunset") { Title = "Info", Type = "text/html" };
        var sunsetPolicy = new SunsetPolicy(sunsetDate, link);
        var opts = new WolverineApiVersioningOptions();
        var state = new ApiVersionEndpointHeaderState(new ApiVersion(1, 0), sunsetPolicy, null);
        var writer = new ApiVersionHeaderWriter(opts);
        var ctx = ContextWithState(state);

        await writer.WriteAsync(ctx);

        ctx.Response.Headers["Link"].ToString()
            .ShouldBe("<https://example.com/info>; rel=\"sunset\"; title=\"Info\"; type=\"text/html\"");
    }

    // 7 — multiple links from one SunsetPolicy are comma-space joined
    [Fact]
    public async Task multiple_links_are_comma_separated()
    {
        var sunsetDate = new DateTimeOffset(2029, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var link1 = new LinkHeaderValue(new Uri("https://example.com/first"), "sunset");
        var link2 = new LinkHeaderValue(new Uri("https://example.com/second"), "sunset");
        var sunsetPolicy = new SunsetPolicy(sunsetDate, link1);
        sunsetPolicy.Links.Add(link2);

        var opts = new WolverineApiVersioningOptions();
        var state = new ApiVersionEndpointHeaderState(new ApiVersion(1, 0), sunsetPolicy, null);
        var writer = new ApiVersionHeaderWriter(opts);
        var ctx = ContextWithState(state);

        await writer.WriteAsync(ctx);

        var linkHeader = ctx.Response.Headers["Link"].ToString();
        linkHeader.ShouldContain("<https://example.com/first>; rel=\"sunset\"");
        linkHeader.ShouldContain("<https://example.com/second>; rel=\"sunset\"");
        linkHeader.ShouldContain(", ");
    }

    // 8 — EmitDeprecationHeaders=false suppresses Deprecation/Sunset/Link; api-supported-versions still emits
    [Fact]
    public async Task disabled_emit_deprecation_headers_skips_deprecation_sunset_and_link()
    {
        var date = new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var opts = new WolverineApiVersioningOptions
        {
            EmitDeprecationHeaders = false,
            EmitApiSupportedVersionsHeader = true
        };
        opts.Sunset("1.0").On(date);

        var sunsetPolicy = opts.SunsetPolicies[new ApiVersion(1, 0)];
        var depPolicy = new DeprecationPolicy(date);
        var linkUri = new Uri("https://example.com");
        var link = new LinkHeaderValue(linkUri, "sunset");
        sunsetPolicy.Links.Add(link);

        var state = new ApiVersionEndpointHeaderState(new ApiVersion(1, 0), sunsetPolicy, depPolicy);
        var writer = new ApiVersionHeaderWriter(opts);
        var ctx = ContextWithState(state);

        await writer.WriteAsync(ctx);

        // api-supported-versions should still be present
        ctx.Response.Headers.ContainsKey("api-supported-versions").ShouldBeTrue();
        // Deprecation/Sunset/Link should be absent
        ctx.Response.Headers.ContainsKey("Deprecation").ShouldBeFalse();
        ctx.Response.Headers.ContainsKey("Sunset").ShouldBeFalse();
        ctx.Response.Headers.ContainsKey("Link").ShouldBeFalse();
    }

    // 9 — EmitApiSupportedVersionsHeader=false suppresses api-supported-versions
    [Fact]
    public async Task disabled_emit_supported_versions_skips_supported_versions()
    {
        var date = new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var opts = new WolverineApiVersioningOptions
        {
            EmitApiSupportedVersionsHeader = false,
            EmitDeprecationHeaders = true
        };
        opts.Deprecate("1.0").On(date);

        var depPolicy = opts.DeprecationPolicies[new ApiVersion(1, 0)];
        var state = new ApiVersionEndpointHeaderState(new ApiVersion(1, 0), null, depPolicy);
        var writer = new ApiVersionHeaderWriter(opts);
        var ctx = ContextWithState(state);

        await writer.WriteAsync(ctx);

        ctx.Response.Headers.ContainsKey("api-supported-versions").ShouldBeFalse();
        // Deprecation still fires
        ctx.Response.Headers.ContainsKey("Deprecation").ShouldBeTrue();
    }
}
