using Asp.Versioning;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Wolverine.Http.AspVersioning.Tests.Parity;

// ---------------------------------------------------------------------------------------------------
// Parity twins. Each logical resource is registered twice against the shared host: once as a vanilla
// Asp.Versioning minimal-API endpoint under /native/*, and once as a Wolverine endpoint under
// /wolverine/* carrying the equivalent [ApiVersion] attributes. Bodies are drawn from ParityPayloads so
// a native endpoint and its Wolverine twin return byte-identical content for the same version.
//
// Reserved API versions (assembly-wide). Every parity host includes THIS whole assembly, so all
// endpoints share one version space per route and one global IApiVersionDescriptionProvider. Tests that
// query that provider by version (OpenApiParityTests' `.Single(d => d.ApiVersion == X)`) need the
// version's *global* aggregate state to be unambiguous, so each number below is reserved to a single
// scenario. Serving one of these from a new endpoint would change its global state and break those
// assertions (often as a confusing `.Single()` throw). Pick a fresh number for new scenarios.
//
//   1.0, 2.0  current/previous pair, reused across routes (provider assertions on it are route-scoped)
//   3.0       advertised twins (served)
//   3.9       advertised-ONLY (advertised by the advertised twins, served by no one → yields no document)
//   5.0       sunset twins (drives the version-keyed Sunset/Link policy)
//   8.0, 8.1  conflict twins (supported-wins reconciliation)
//   10.0      deprecated twins (the whole 10.0 group is deprecated)
// ---------------------------------------------------------------------------------------------------

internal static class ParityPayloads
{
    public const string OrdersV1 = "orders-v1";
    public const string OrdersV2 = "orders-v2";
    public const string Health = "health-ok";
    public const string Ping = "ping";
    public const string Sunset = "sunset-v5";
    public const string Advertised = "advertised-v3";
    public const string V1Only = "v1only";
    public const string MapToV1 = "mapto-v1";
    public const string MapToV2 = "mapto-v2";
    public const string ConflictSupported = "conflict-supported";
    public const string ConflictDeprecated = "conflict-deprecated";
    public const string ConflictProbe = "conflict-probe";
    public const string Deprecated = "deprecated-v10";
}

/// <summary>
/// The native (vanilla Asp.Versioning minimal-API) half of the parity twins: one <c>ApiVersionSet</c>
/// per (verb, route) declaring exactly the versions that route implements, so native's
/// <c>api-supported-versions</c> lines up with the bridge's per-route grouping.
/// </summary>
internal static class ParityEndpoints
{
    private static readonly ApiVersion V1 = new(1, 0);
    private static readonly ApiVersion V2 = new(2, 0);
    private static readonly ApiVersion V3 = new(3, 0);
    private static readonly ApiVersion V3_9 = new(3, 9);
    private static readonly ApiVersion V5 = new(5, 0);
    private static readonly ApiVersion V8 = new(8, 0);
    private static readonly ApiVersion V8_1 = new(8, 1);
    private static readonly ApiVersion V10 = new(10, 0);

    public static void MapNative(IEndpointRouteBuilder app)
    {
        // /native/orders — v1.0 deprecated, v2.0 current (single set spanning the route).
        var ordersSet = app.NewApiVersionSet()
            .HasApiVersion(V2)
            .HasDeprecatedApiVersion(V1)
            .ReportApiVersions()
            .Build();

        app.MapGet("/native/orders", () => Results.Text(ParityPayloads.OrdersV1))
            .WithApiVersionSet(ordersSet)
            .MapToApiVersion(V1);
        app.MapGet("/native/orders", () => Results.Text(ParityPayloads.OrdersV2))
            .WithApiVersionSet(ordersSet)
            .MapToApiVersion(V2);

        // /native/health — version neutral. Asp.Versioning requires a version set before
        // IsApiVersionNeutral is applied, mirroring the bridge (attach set, then mark neutral).
        var neutralSet = app.NewApiVersionSet().Build();
        app.MapGet("/native/health", () => Results.Text(ParityPayloads.Health))
            .WithApiVersionSet(neutralSet)
            .IsApiVersionNeutral();

        // /native/ping — completely unversioned (no version metadata at all).
        app.MapGet("/native/ping", () => Results.Text(ParityPayloads.Ping));

        // /native/sunset — serves 5.0 only; the host's version-keyed sunset policy for 5.0 emits the
        // Sunset + Link headers. 5.0 is unique to the sunset twins.
        var sunsetSet = app.NewApiVersionSet().HasApiVersion(V5).ReportApiVersions().Build();
        app.MapGet("/native/sunset", () => Results.Text(ParityPayloads.Sunset))
            .WithApiVersionSet(sunsetSet)
            .MapToApiVersion(V5);

        // /native/advertised — implements 3.0, advertises 3.9 (implemented by no one). Advertised folds
        // into api-supported-versions, so the header reports both 3.0 and 3.9.
        var advertisedSet = app.NewApiVersionSet()
            .HasApiVersion(V3)
            .AdvertisesApiVersion(V3_9)
            .ReportApiVersions()
            .Build();
        app.MapGet("/native/advertised", () => Results.Text(ParityPayloads.Advertised))
            .WithApiVersionSet(advertisedSet)
            .MapToApiVersion(V3);

        // /native/v1only — serves 1.0 only, on its own route (per-version exclusivity twin for the
        // OpenAPI grouping parity: appears in the v1 document group and NOT the v2 one).
        var v1OnlySet = app.NewApiVersionSet().HasApiVersion(V1).ReportApiVersions().Build();
        app.MapGet("/native/v1only", () => Results.Text(ParityPayloads.V1Only))
            .WithApiVersionSet(v1OnlySet)
            .MapToApiVersion(V1);

        // /native/mapto — one set declaring {1.0, 2.0} spanning the route, two endpoints each
        // MapToApiVersion'd. The Wolverine twin is one class whose class-level [ApiVersion] declares the
        // same version space with per-method [MapToApiVersion].
        var mapToSet = app.NewApiVersionSet()
            .HasApiVersion(V1)
            .HasApiVersion(V2)
            .ReportApiVersions()
            .Build();
        app.MapGet("/native/mapto", () => Results.Text(ParityPayloads.MapToV1))
            .WithApiVersionSet(mapToSet)
            .MapToApiVersion(V1);
        app.MapGet("/native/mapto", () => Results.Text(ParityPayloads.MapToV2))
            .WithApiVersionSet(mapToSet)
            .MapToApiVersion(V2);

        // /native/conflict — declares the resolved state a correct consumer would author: 8.0 and 8.1 both
        // supported. The Wolverine twin reaches the same state via supported-wins (one sibling declares 8.0
        // supported, another declares 8.0 deprecated). 8.1 is the unambiguous probe. 8.0/8.1 are unique to
        // the conflict twins.
        var conflictSet = app.NewApiVersionSet()
            .HasApiVersion(V8)
            .HasApiVersion(V8_1)
            .ReportApiVersions()
            .Build();
        app.MapGet("/native/conflict", () => Results.Text(ParityPayloads.ConflictSupported))
            .WithApiVersionSet(conflictSet)
            .MapToApiVersion(V8);
        app.MapGet("/native/conflict", () => Results.Text(ParityPayloads.ConflictProbe))
            .WithApiVersionSet(conflictSet)
            .MapToApiVersion(V8_1);

        // /native/deprecated — serves 10.0 as the only (deprecated) version, so the 10.0 document group is
        // deprecated and its operation is marked deprecated. 10.0 is unique to the deprecated twins.
        var deprecatedSet = app.NewApiVersionSet()
            .HasDeprecatedApiVersion(V10)
            .ReportApiVersions()
            .Build();
        app.MapGet("/native/deprecated", () => Results.Text(ParityPayloads.Deprecated))
            .WithApiVersionSet(deprecatedSet)
            .MapToApiVersion(V10);

        // /native/feature — serves {1.0, 2.0} and echoes the version the matcher resolved (read off
        // IApiVersioningFeature), to prove the feature is populated identically for a Wolverine endpoint.
        var featureSet = app.NewApiVersionSet()
            .HasApiVersion(V1)
            .HasApiVersion(V2)
            .ReportApiVersions()
            .Build();
        app.MapGet(
                "/native/feature",
                (HttpContext context) =>
                    Results.Text(
                        context
                            .Features.Get<IApiVersioningFeature>()
                            ?.RequestedApiVersion?.ToString()
                            ?? "none"
                    )
            )
            .WithApiVersionSet(featureSet)
            .MapToApiVersion(V1)
            .MapToApiVersion(V2);
    }

    // A native twin for the dedicated-host tiers: one set declaring {1.0, 2.0} spanning the route, two
    // endpoints each mapped to its version. Route template + bodies are the only per-host difference.
    public static void MapTwoVersionRoute(
        IEndpointRouteBuilder app,
        string route,
        string v1Body,
        string v2Body
    )
    {
        var set = app.NewApiVersionSet()
            .HasApiVersion(V1)
            .HasApiVersion(V2)
            .ReportApiVersions()
            .Build();

        app.MapGet(route, () => Results.Text(v1Body)).WithApiVersionSet(set).MapToApiVersion(V1);
        app.MapGet(route, () => Results.Text(v2Body)).WithApiVersionSet(set).MapToApiVersion(V2);
    }
}

// ---------------------------------------------------------------------------------------------------
// Wolverine half of the twins. Public so Wolverine HTTP discovery maps them.
// ---------------------------------------------------------------------------------------------------

public class WolverineOrdersV1ParityEndpoint
{
    [WolverineGet("/wolverine/orders")]
    [ApiVersion("1.0", Deprecated = true)]
    public string Get() => ParityPayloads.OrdersV1;
}

public class WolverineOrdersV2ParityEndpoint
{
    [WolverineGet("/wolverine/orders")]
    [ApiVersion("2.0")]
    public string Get() => ParityPayloads.OrdersV2;
}

public class WolverineHealthParityEndpoint
{
    [WolverineGet("/wolverine/health")]
    [ApiVersionNeutral]
    public string Get() => ParityPayloads.Health;
}

public class WolverinePingParityEndpoint
{
    [WolverineGet("/wolverine/ping")]
    public string Get() => ParityPayloads.Ping;
}

public class WolverineSunsetParityEndpoint
{
    [WolverineGet("/wolverine/sunset")]
    [ApiVersion("5.0")]
    public string Get() => ParityPayloads.Sunset;
}

public class WolverineAdvertisedParityEndpoint
{
    [WolverineGet("/wolverine/advertised")]
    [ApiVersion("3.0")]
    [AdvertiseApiVersions("3.9")]
    public string Get() => ParityPayloads.Advertised;
}

public class WolverineV1OnlyParityEndpoint
{
    [WolverineGet("/wolverine/v1only")]
    [ApiVersion("1.0")]
    public string Get() => ParityPayloads.V1Only;
}

// One class whose class-level [ApiVersion] declares the version space, with per-method [MapToApiVersion]
// selecting each subset. The two methods share one route, so the bridge folds them into one set declaring
// {1.0, 2.0} — matching the native /native/mapto set.
[ApiVersion("1.0")]
[ApiVersion("2.0")]
public class WolverineMapToParityEndpoint
{
    [WolverineGet("/wolverine/mapto")]
    [MapToApiVersion("1.0")]
    public string GetV1() => ParityPayloads.MapToV1;

    [WolverineGet("/wolverine/mapto")]
    [MapToApiVersion("2.0")]
    public string GetV2() => ParityPayloads.MapToV2;
}

// /wolverine/conflict — 8.0 is supported by one sibling and deprecated by another; supported-wins folds
// 8.0 into the supported bucket (never both). 8.1 is an unambiguous probe. The resolved state matches the
// native /native/conflict set (8.0 + 8.1 supported).
public class WolverineConflictSupportedEndpoint
{
    [WolverineGet("/wolverine/conflict")]
    [ApiVersion("8.0")]
    public string Get() => ParityPayloads.ConflictSupported;
}

public class WolverineConflictDeprecatedEndpoint
{
    [WolverineGet("/wolverine/conflict")]
    [ApiVersion("8.0", Deprecated = true)]
    public string Get() => ParityPayloads.ConflictDeprecated;
}

public class WolverineConflictProbeEndpoint
{
    [WolverineGet("/wolverine/conflict")]
    [ApiVersion("8.1")]
    public string Get() => ParityPayloads.ConflictProbe;
}

public class WolverineDeprecatedParityEndpoint
{
    [WolverineGet("/wolverine/deprecated")]
    [ApiVersion("10.0", Deprecated = true)]
    public string Get() => ParityPayloads.Deprecated;
}

public class WolverineFeatureParityEndpoint
{
    [WolverineGet("/wolverine/feature")]
    [ApiVersion("1.0")]
    [ApiVersion("2.0")]
    public string Get(HttpContext context) =>
        context.Features.Get<IApiVersioningFeature>()?.RequestedApiVersion?.ToString() ?? "none";
}
