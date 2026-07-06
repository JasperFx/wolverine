using Alba;
using Asp.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Wolverine.Http.AspVersioning.Tests.Parity;

// ---------------------------------------------------------------------------------------------------
// Parity for two Asp.Versioning knobs that are GLOBAL ApiVersioningOptions settings and therefore need
// their own hosts (they change behavior app-wide): a media-type-ONLY reader (whose miss path is
// 415/406, not 400) and AssumeDefaultVersionWhenUnspecified. Each host registers a native /native/* twin
// beside the Wolverine /wolverine/* twin and asserts identical behavior.
// ---------------------------------------------------------------------------------------------------

internal static class ParityProbe
{
    public static async Task<(int Status, string Body)> Get(
        IAlbaHost host,
        Action<Scenario> configure
    )
    {
        var result = await host.Scenario(x =>
        {
            configure(x);
            x.IgnoreStatusCode();
        });
        return (result.Context.Response.StatusCode, (await result.ReadAsTextAsync()).Trim());
    }
}

// ---------- Media-type-only host (miss path is 415/406) ----------

public class MediaTypeOnlyParityFixture : ParityFixture
{
    public override Task InitializeAsync() =>
        BuildHost(
            services =>
                services.AddApiVersioning(options =>
                {
                    options.ReportApiVersions = true;
                    options.ApiVersionReader = new MediaTypeApiVersionReader(); // media-type ONLY → miss = 415/406
                }),
            app =>
                ParityEndpoints.MapTwoVersionRoute(app, "/native/mtonly", "mtonly-v1", "mtonly-v2")
        );
}

[CollectionDefinition("mediatype-only")]
public class MediaTypeOnlyParityCollection : ICollectionFixture<MediaTypeOnlyParityFixture>;

public class WolverineMediaTypeOnlyV1Endpoint
{
    [WolverineGet("/wolverine/mtonly")]
    [ApiVersion("1.0")]
    public string Get() => "mtonly-v1";
}

public class WolverineMediaTypeOnlyV2Endpoint
{
    [WolverineGet("/wolverine/mtonly")]
    [ApiVersion("2.0")]
    public string Get() => "mtonly-v2";
}

[Collection("mediatype-only")]
public class MediaTypeOnlyParityTests
{
    private readonly IAlbaHost _host;

    public MediaTypeOnlyParityTests(MediaTypeOnlyParityFixture fixture) => _host = fixture.Host;

    // A valid media-type version routes identically on both twins.
    [Fact]
    public async Task media_type_version_routes_identically()
    {
        var native = await ParityProbe.Get(
            _host,
            x =>
            {
                x.Get.Url("/native/mtonly");
                x.WithRequestHeader("Accept", "application/json;v=1.0");
            }
        );
        var wolverine = await ParityProbe.Get(
            _host,
            x =>
            {
                x.Get.Url("/wolverine/mtonly");
                x.WithRequestHeader("Accept", "application/json;v=1.0");
            }
        );

        native.Status.ShouldBe(200);
        wolverine.Status.ShouldBe(native.Status);
        wolverine.Body.ShouldBe(native.Body);
        wolverine.Body.ShouldBe("mtonly-v1");
    }

    // Under a media-type-ONLY reader, an unsupported media-type version takes Asp.Versioning's rejection
    // path. The assertion is parity: whatever status the library chooses (400/406/415), the Wolverine twin
    // returns the same one.
    [Fact]
    public async Task media_type_unsupported_version_error_matches_native()
    {
        var native = await ParityProbe.Get(
            _host,
            x =>
            {
                x.Get.Url("/native/mtonly");
                x.WithRequestHeader("Accept", "application/json;v=9.0");
            }
        );
        var wolverine = await ParityProbe.Get(
            _host,
            x =>
            {
                x.Get.Url("/wolverine/mtonly");
                x.WithRequestHeader("Accept", "application/json;v=9.0");
            }
        );

        wolverine.Status.ShouldBe(native.Status);
        native.Status.ShouldBeGreaterThanOrEqualTo(400);
    }
}

// ---------- AssumeDefaultVersionWhenUnspecified host ----------

public class AssumeDefaultParityFixture : ParityFixture
{
    public override Task InitializeAsync() =>
        BuildHost(
            services =>
                services.AddApiVersioning(options =>
                {
                    options.ReportApiVersions = true;
                    options.AssumeDefaultVersionWhenUnspecified = true;
                    options.DefaultApiVersion = new ApiVersion(1, 0);
                }),
            app =>
                ParityEndpoints.MapTwoVersionRoute(app, "/native/assumedefault", "ad-v1", "ad-v2")
        );
}

[CollectionDefinition("assume-default")]
public class AssumeDefaultParityCollection : ICollectionFixture<AssumeDefaultParityFixture>;

public class WolverineAssumeDefaultV1Endpoint
{
    [WolverineGet("/wolverine/assumedefault")]
    [ApiVersion("1.0")]
    public string Get() => "ad-v1";
}

public class WolverineAssumeDefaultV2Endpoint
{
    [WolverineGet("/wolverine/assumedefault")]
    [ApiVersion("2.0")]
    public string Get() => "ad-v2";
}

[Collection("assume-default")]
public class AssumeDefaultParityTests
{
    private readonly IAlbaHost _host;

    public AssumeDefaultParityTests(AssumeDefaultParityFixture fixture) => _host = fixture.Host;

    // With AssumeDefaultVersionWhenUnspecified = true, a request specifying no version routes to the
    // default (1.0) on both twins (rather than the 400 the default config returns).
    [Fact]
    public async Task unspecified_version_routes_to_default_on_both()
    {
        var native = await ParityProbe.Get(_host, x => x.Get.Url("/native/assumedefault"));
        var wolverine = await ParityProbe.Get(_host, x => x.Get.Url("/wolverine/assumedefault"));

        native.Status.ShouldBe(200);
        wolverine.Status.ShouldBe(native.Status);
        wolverine.Body.ShouldBe(native.Body);
        wolverine.Body.ShouldBe("ad-v1");
    }
}
