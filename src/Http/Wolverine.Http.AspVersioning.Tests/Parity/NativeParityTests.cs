using Alba;
using Shouldly;

namespace Wolverine.Http.AspVersioning.Tests.Parity;

/// <summary>
/// The focused parity tier: for each logical resource a vanilla Asp.Versioning minimal-API endpoint
/// (<c>/native/*</c>) and its Wolverine twin (<c>/wolverine/*</c>) are registered against the same host
/// (see <see cref="ParityEndpoints"/>). Each test hits both and asserts the surfaces the bridge owns —
/// routing/body, version-report headers, error status — are identical.
///
/// Content-Type is intentionally not compared: the version reader is orthogonal to the bridge, and the
/// base Content-Type differs by charset between a native <c>Results.Text</c> and a Wolverine string
/// result independently of versioning.
/// </summary>
public class NativeParityTests : AspVersioningIntegrationContext
{
    public NativeParityTests(AspVersioningAppFixture fixture)
        : base(fixture) { }

    private sealed record Captured(
        int Status,
        string Body,
        string Supported,
        string Deprecated,
        string Sunset,
        string Link,
        string ContentType
    );

    private Task<Captured> Get(string url) => Capture(x => x.Get.Url(url), assertOk: true);

    // Error-path variant: does not assert 200, so the twins can be compared on responses that fail.
    private Task<Captured> GetRaw(string url) => Capture(x => x.Get.Url(url), assertOk: false);

    private async Task<Captured> Capture(Action<Scenario> configure, bool assertOk)
    {
        var result = await Scenario(x =>
        {
            configure(x);
            if (assertOk)
            {
                x.StatusCodeShouldBeOk();
            }
            else
            {
                x.IgnoreStatusCode();
            }
        });

        var headers = result.Context.Response.Headers;
        return new Captured(
            result.Context.Response.StatusCode,
            (await result.ReadAsTextAsync()).Trim(),
            headers["api-supported-versions"].ToString(),
            headers["api-deprecated-versions"].ToString(),
            headers["Sunset"].ToString(),
            headers["Link"].ToString(),
            result.Context.Response.ContentType ?? ""
        );
    }

    // The individual version tokens of a comma-delimited version header. Membership and equality checks
    // run on these rather than the raw string, so a substring can't satisfy them (e.g. "11.0" must not
    // count as containing "1.0"); equality is order-insensitive via ShouldBe(..., ignoreOrder: true).
    private static string[] Tokens(string headerValue) =>
        headerValue.Split(
            ',',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
        );

    // A v1 request reaches the v1 endpoint and a v2 request reaches v2, identically to native — the
    // foundational proof that Asp.Versioning's matcher honors the metadata the bridge attaches.
    [Theory]
    [InlineData("1.0", "orders-v1")]
    [InlineData("2.0", "orders-v2")]
    public async Task requested_version_routes_and_body_matches_native(
        string version,
        string expected
    )
    {
        var native = await Get($"/native/orders?api-version={version}");
        var wolverine = await Get($"/wolverine/orders?api-version={version}");

        wolverine.Status.ShouldBe(native.Status);
        wolverine.Body.ShouldBe(native.Body);
        wolverine.Body.ShouldBe(expected);
    }

    // The api-supported-versions / api-deprecated-versions headers are identical between the twins.
    [Fact]
    public async Task version_report_headers_match_native()
    {
        var native = await Get("/native/orders?api-version=2.0");
        var wolverine = await Get("/wolverine/orders?api-version=2.0");

        Tokens(wolverine.Supported).ShouldBe(Tokens(native.Supported), ignoreOrder: true);
        Tokens(wolverine.Deprecated).ShouldBe(Tokens(native.Deprecated), ignoreOrder: true);
    }

    // 1.0 is reported deprecated and excluded from supported on both twins (supported-wins).
    [Fact]
    public async Task deprecated_version_reported_identically()
    {
        var native = await Get("/native/orders?api-version=1.0");
        var wolverine = await Get("/wolverine/orders?api-version=1.0");

        wolverine.Body.ShouldBe(native.Body);
        Tokens(wolverine.Supported).ShouldBe(Tokens(native.Supported), ignoreOrder: true);
        Tokens(wolverine.Deprecated).ShouldBe(Tokens(native.Deprecated), ignoreOrder: true);

        // Substantive pin: 1.0 deprecated, 2.0 supported, on both.
        Tokens(wolverine.Deprecated).ShouldContain("1.0");
        Tokens(native.Deprecated).ShouldContain("1.0");
        Tokens(wolverine.Supported).ShouldContain("2.0");
        Tokens(native.Supported).ShouldContain("2.0");
        Tokens(wolverine.Supported).ShouldNotContain("1.0");
        Tokens(native.Supported).ShouldNotContain("1.0");
    }

    // A version-neutral route is reachable and behaves identically on both twins.
    [Fact]
    public async Task version_neutral_route_behaves_identically()
    {
        var native = await Get("/native/health");
        var wolverine = await Get("/wolverine/health");

        wolverine.Status.ShouldBe(native.Status);
        wolverine.Body.ShouldBe(native.Body);
        wolverine.Body.ShouldBe("health-ok");
    }

    // An unversioned route serves normally and emits no version headers on both.
    [Fact]
    public async Task unversioned_route_untouched_on_both()
    {
        var native = await Get("/native/ping");
        var wolverine = await Get("/wolverine/ping");

        wolverine.Status.ShouldBe(native.Status);
        wolverine.Body.ShouldBe(native.Body);
        wolverine.Supported.ShouldBeNullOrEmpty();
        native.Supported.ShouldBeNullOrEmpty();
    }

    // An unsupported version yields the same error status and api-supported-versions header on both.
    // Bodies are ProblemDetails with a per-request traceId, so status + header are compared, not the body.
    [Fact]
    public async Task unsupported_version_error_matches_native()
    {
        var native = await GetRaw("/native/orders?api-version=9.0");
        var wolverine = await GetRaw("/wolverine/orders?api-version=9.0");

        wolverine.Status.ShouldBe(native.Status);
        wolverine.Status.ShouldBeGreaterThanOrEqualTo(400);
        Tokens(wolverine.Supported).ShouldBe(Tokens(native.Supported), ignoreOrder: true);
    }

    // With AssumeDefaultVersionWhenUnspecified = false (the default), a request with no version behaves
    // identically on both twins (vanilla Asp.Versioning returns 400).
    [Fact]
    public async Task missing_version_behaves_identically_to_native()
    {
        var native = await GetRaw("/native/orders");
        var wolverine = await GetRaw("/wolverine/orders");

        // Substantive pin (not just twin-equality): the default config rejects a versionless request.
        native.Status.ShouldBeGreaterThanOrEqualTo(400);
        wolverine.Status.ShouldBe(native.Status);
        Tokens(wolverine.Supported).ShouldBe(Tokens(native.Supported), ignoreOrder: true);
    }

    // The `api-version` request-header reader routes and reports identically.
    [Fact]
    public async Task header_reader_routes_and_reports_identically()
    {
        var native = await Capture(
            x =>
            {
                x.Get.Url("/native/orders");
                x.WithRequestHeader("api-version", "2.0");
            },
            assertOk: true
        );
        var wolverine = await Capture(
            x =>
            {
                x.Get.Url("/wolverine/orders");
                x.WithRequestHeader("api-version", "2.0");
            },
            assertOk: true
        );

        wolverine.Body.ShouldBe(native.Body);
        wolverine.Body.ShouldBe(ParityPayloads.OrdersV2);
        Tokens(wolverine.Supported).ShouldBe(Tokens(native.Supported), ignoreOrder: true);
    }

    // The media-type reader (an `Accept` `v=` parameter) routes identically and echoes the negotiated
    // version into the response Content-Type on both. Only the version echo is asserted, not the full
    // Content-Type: the base value differs by charset (native `text/plain; charset=utf-8` vs Wolverine
    // `text/plain`), which is unrelated to versioning.
    [Fact]
    public async Task media_type_reader_routes_and_echoes_version_identically()
    {
        var native = await Capture(
            x =>
            {
                x.Get.Url("/native/orders");
                x.WithRequestHeader("Accept", "application/json;v=2.0");
            },
            assertOk: true
        );
        var wolverine = await Capture(
            x =>
            {
                x.Get.Url("/wolverine/orders");
                x.WithRequestHeader("Accept", "application/json;v=2.0");
            },
            assertOk: true
        );

        wolverine.Body.ShouldBe(native.Body);
        wolverine.Body.ShouldBe(ParityPayloads.OrdersV2);
        wolverine.ContentType.ShouldContain("v=2.0");
        native.ContentType.ShouldContain("v=2.0");
    }

    // A version-keyed sunset policy emits identical Sunset + Link headers on both. The bridge builds an
    // unnamed set and the native twin uses one too, so a null-named/version-keyed policy applies to both.
    [Fact]
    public async Task sunset_policy_headers_match_native()
    {
        var native = await Get("/native/sunset?api-version=5.0");
        var wolverine = await Get("/wolverine/sunset?api-version=5.0");

        wolverine.Sunset.ShouldBe(native.Sunset);
        wolverine.Sunset.ShouldNotBeNullOrEmpty();
        wolverine.Link.ShouldBe(native.Link);
        wolverine.Link.ShouldContain("https://example.com/sunset");
    }

    // An advertised (implemented-by-no-one) version folds into api-supported-versions identically on both.
    [Fact]
    public async Task advertised_version_reported_identically()
    {
        var native = await Get("/native/advertised?api-version=3.0");
        var wolverine = await Get("/wolverine/advertised?api-version=3.0");

        Tokens(wolverine.Supported).ShouldBe(Tokens(native.Supported), ignoreOrder: true);
        Tokens(wolverine.Supported).ShouldContain("3.0");
        Tokens(wolverine.Supported).ShouldContain("3.9");
    }

    // Two distinct versions (query + header) is ambiguous and yields the same error status on both.
    [Fact]
    public async Task ambiguous_version_error_matches_native()
    {
        var native = await Capture(
            x =>
            {
                x.Get.Url("/native/orders?api-version=1.0");
                x.WithRequestHeader("api-version", "2.0");
            },
            assertOk: false
        );
        var wolverine = await Capture(
            x =>
            {
                x.Get.Url("/wolverine/orders?api-version=1.0");
                x.WithRequestHeader("api-version", "2.0");
            },
            assertOk: false
        );

        wolverine.Status.ShouldBe(native.Status);
        wolverine.Status.ShouldBeGreaterThanOrEqualTo(400);
    }

    // A malformed version string yields the same error status on both.
    [Fact]
    public async Task malformed_version_error_matches_native()
    {
        var native = await GetRaw("/native/orders?api-version=not-a-version");
        var wolverine = await GetRaw("/wolverine/orders?api-version=not-a-version");

        wolverine.Status.ShouldBe(native.Status);
        wolverine.Status.ShouldBeGreaterThanOrEqualTo(400);
    }

    // [MapToApiVersion] authoring parity: a class-level [ApiVersion] declaring {1.0, 2.0} with per-method
    // [MapToApiVersion] routes identically to a native shared-set + per-endpoint MapToApiVersion, and
    // reports the same supported versions.
    [Theory]
    [InlineData("1.0", "mapto-v1")]
    [InlineData("2.0", "mapto-v2")]
    public async Task map_to_api_version_routes_and_body_matches_native(
        string version,
        string expected
    )
    {
        var native = await Get($"/native/mapto?api-version={version}");
        var wolverine = await Get($"/wolverine/mapto?api-version={version}");

        wolverine.Status.ShouldBe(native.Status);
        wolverine.Body.ShouldBe(native.Body);
        wolverine.Body.ShouldBe(expected);
        Tokens(wolverine.Supported).ShouldBe(Tokens(native.Supported), ignoreOrder: true);
    }

    // /wolverine/conflict declares 8.0 supported on one sibling and 8.0 deprecated on another;
    // supported-wins folds 8.0 into api-supported-versions and out of api-deprecated-versions. The native
    // twin declares the resolved state directly (8.0 + 8.1 supported), so the version headers must match.
    // 8.1 is the unambiguous probe; the headers describe the whole shared set regardless.
    [Fact]
    public async Task supported_wins_reported_identically_to_native()
    {
        var native = await Get("/native/conflict?api-version=8.1");
        var wolverine = await Get("/wolverine/conflict?api-version=8.1");

        wolverine.Body.ShouldBe(native.Body);
        Tokens(wolverine.Supported).ShouldBe(Tokens(native.Supported), ignoreOrder: true);
        Tokens(wolverine.Deprecated).ShouldBe(Tokens(native.Deprecated), ignoreOrder: true);

        // Substantive pin: 8.0 is reported supported, never deprecated, on both.
        Tokens(wolverine.Supported).ShouldContain("8.0");
        Tokens(native.Supported).ShouldContain("8.0");
        Tokens(wolverine.Deprecated).ShouldNotContain("8.0");
        Tokens(native.Deprecated).ShouldNotContain("8.0");
    }

    // The matcher populates IApiVersioningFeature for a Wolverine endpoint identically to a native one:
    // both /native/feature and /wolverine/feature (each serving {1.0, 2.0}) echo back the resolved version.
    [Theory]
    [InlineData("1.0")]
    [InlineData("2.0")]
    public async Task resolved_version_feature_matches_native(string version)
    {
        var native = await Get($"/native/feature?api-version={version}");
        var wolverine = await Get($"/wolverine/feature?api-version={version}");

        wolverine.Body.ShouldBe(native.Body);
        wolverine.Body.ShouldBe(version);
    }
}
