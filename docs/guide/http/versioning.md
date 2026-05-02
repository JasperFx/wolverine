# HTTP API Versioning <Badge type="tip" text="5.36" />

Wolverine.Http provides native API versioning support that lets you evolve your HTTP services over time without
breaking existing clients. Versioned endpoints coexist at separate URL prefixes (e.g. `/v1/orders` and `/v2/orders`),
carry the correct OpenAPI metadata, and can emit RFC 9745 `Deprecation` and RFC 8594 `Sunset` response headers to
signal planned end-of-life to callers.

The feature depends on the `Asp.Versioning.Abstractions` 10.x NuGet package — the thin, framework-neutral abstraction
layer. It does **not** require `Asp.Versioning.Http` (the ASP.NET Core-specific middleware pack). Wolverine drives
versioning entirely through its own `IHttpPolicy` pipeline, so there is no conflict with an existing
`AddApiVersioning()` registration, and no additional ASP.NET Core middleware is needed.

::: info
This release supports **URL-segment versioning** (e.g. `/v1/...`, `/v2/...`). Each endpoint declares a single
`[ApiVersion]`; multi-version handlers via `[MapToApiVersion]` are not supported.
:::

## Quick Start

**1. Add the package** (if not already present via `WolverineFx.Http`):

```bash
dotnet add package Asp.Versioning.Abstractions
```

**2. Decorate your endpoint class or method:**

```csharp
using Asp.Versioning;
using Wolverine.Http;

[ApiVersion("1.0")]
public static class OrdersV1Endpoint
{
    [WolverineGet("/orders", OperationId = "OrdersV1Endpoint.Get")]
    public static OrdersV1Response Get() => new(["order-a", "order-b"]);
}

public record OrdersV1Response(IReadOnlyList<string> Orders);
```

**3. Enable versioning inside `MapWolverineEndpoints`:**

```csharp
app.MapWolverineEndpoints(opts =>
{
    opts.UseApiVersioning(v =>
    {
        // All options are optional — the defaults work out of the box.
        v.UnversionedPolicy = UnversionedPolicy.PassThrough;
    });
});
```

With these two pieces in place Wolverine will rewrite the route to `/v1/orders` and attach the appropriate
`ApiVersionMetadata` and OpenAPI group name automatically.

## Declaring Versions

### Attribute placement

Apply `[ApiVersion]` to the endpoint class or to the handler method. **Method-level wins over class-level:**
if both declare a version, the method attribute is used.

```csharp
// Class-level — all methods in this class are v2.
[ApiVersion("2.0")]
public static class OrdersV2Endpoint
{
    [WolverineGet("/orders")]
    public static OrdersV2Response Get() => new("ok", ["v2-a", "v2-b"]);
}
```

### One version per endpoint

Declaring **multiple** `[ApiVersion]` attributes on the same handler method is not supported in v1. The resolver
picks the first attribute encountered and ignores any additional ones. If you have two incompatible response shapes
for the same resource, create separate endpoint classes — one per version — as shown in the sample app
(`OrdersV1Endpoint`, `OrdersV2Endpoint`, `OrdersV3PreviewEndpoint`).

### Marking a version as deprecated via attribute

The `Deprecated` property on `[ApiVersion]` is honoured and will automatically attach a deprecation policy to
the chain:

```csharp
[ApiVersion("1.0", Deprecated = true)]
public static class LegacyOrdersEndpoint
{
    [WolverineGet("/orders")]
    public static OrdersV1Response Get() => new([]);
}
```

When this attribute is present, Wolverine emits a `Deprecation: true` response header (RFC 9745) and marks the
OpenAPI operation as deprecated without any additional configuration.

## URL-Segment Versioning

### Default behaviour

By default, Wolverine prepends `v{major}` to every versioned route. Given `[ApiVersion("2.0")]` and the route
`/orders`, the live URL becomes `/v2/orders`.

### Customising the URL segment prefix

The prefix template is controlled by `UrlSegmentPrefix` (default `"v{version}"`). The `{version}` token is
**required** — omitting it causes a startup exception (see [Troubleshooting](#troubleshooting)):

```csharp
opts.UseApiVersioning(v =>
{
    // Changes /v2/orders → /api/v2/orders
    v.UrlSegmentPrefix = "api/v{version}";
});
```

Set `UrlSegmentPrefix = null` to disable URL-segment versioning entirely. In that case the original route is
kept unchanged and no prefix is injected.

### Version format in the URL

The `UrlSegmentVersionFormatter` callback controls how an `ApiVersion` is turned into the string substituted
into the prefix. The default formats `ApiVersion(2, 0)` as `"2"` (major-only), giving clean URLs like `/v2/`.

Override it to emit `major.minor` format:

```csharp
opts.UseApiVersioning(v =>
{
    // Produces /v2.0/orders instead of /v2/orders
    v.UrlSegmentVersionFormatter = apiVersion =>
        apiVersion.MajorVersion.HasValue
            ? $"{apiVersion.MajorVersion}.{apiVersion.MinorVersion ?? 0}"
            : apiVersion.ToString();
});
```

For date-based versions where `MajorVersion` is `null`, the default formatter falls back to `ApiVersion.ToString()`,
which may include hyphens (e.g. `2024-01-01`). Override the formatter if your URL scheme needs a different shape.

## Unversioned-Endpoint Policy

When `UseApiVersioning` is active, every endpoint that lacks an `[ApiVersion]` attribute is subject to the
`UnversionedPolicy`. Three behaviours are available:

| Policy | Behaviour |
|---|---|
| `PassThrough` *(default)* | Unversioned endpoints stay at their declared route with no version metadata. They coexist alongside versioned endpoints. |
| `RequireExplicit` | Bootstrap throws an `InvalidOperationException` if any endpoint is missing `[ApiVersion]`. Recommended for greenfield APIs that should be fully versioned from day one. |
| `AssignDefault` | All unversioned endpoints are silently promoted to `DefaultVersion`. Set `DefaultVersion` when using this mode. |

```csharp
// RequireExplicit — every endpoint must carry [ApiVersion]
opts.UseApiVersioning(v =>
{
    v.UnversionedPolicy = UnversionedPolicy.RequireExplicit;
});

// AssignDefault — migrate an existing unversioned API to a v1 baseline
opts.UseApiVersioning(v =>
{
    v.UnversionedPolicy = UnversionedPolicy.AssignDefault;
    v.DefaultVersion = new ApiVersion(1, 0);
});
```

## Version-Neutral Endpoints <Badge type="tip" text="5.36" />

Some endpoints — health checks, webhooks, metrics, infrastructure utilities — are intentionally outside the
versioning lifecycle. Mark them with `[ApiVersionNeutral]` to opt out explicitly:

- The chain keeps its declared route — no `/v1`, `/v2`, etc. prefix is injected.
- No `Deprecation`, `Sunset`, `Link`, or `api-supported-versions` headers are emitted.
- The chain is excluded from duplicate-detection on the version axis (a neutral chain at the same
  `(verb, route)` as a versioned chain does **not** collide).
- The chain satisfies `UnversionedPolicy.RequireExplicit` — neutrality is treated as an explicit choice,
  not a missing annotation.

### Class-level neutrality

```csharp
[ApiVersionNeutral]
public static class HealthCheckEndpoint
{
    [WolverineGet("/health")]
    public static HealthCheckResponse Get() => new("ok");
}
```

### Method-level neutrality

A single utility method on an otherwise versioned controller can opt out without affecting its siblings.
Method-level `[ApiVersionNeutral]` overrides the class-level `[ApiVersion]` for that method only:

```csharp
[ApiVersion("1.0")]
public static class CustomersEndpoint
{
    // Versioned at v1.0, lives at /v1/customers
    [WolverineGet("/customers")]
    public static CustomerList GetCustomers() => /* ... */;

    // Version-neutral; lives at /customers/ping with no version-related headers
    [ApiVersionNeutral]
    [WolverineGet("/customers/ping")]
    public static string Ping() => "pong";
}
```

Combining `[ApiVersion]` and `[ApiVersionNeutral]` on **the same target** (same method or same class) is
contradictory and fails fast at startup with an `InvalidOperationException`.

### OpenAPI integration

Neutral chains receive `Asp.Versioning.ApiVersionMetadata.Neutral` (so `IsApiVersionNeutral` is `true` on
the metadata), but they deliberately get no `IEndpointGroupNameMetadata`. Without a group name they do not
match the default Swashbuckle partitioning (`api.GroupName == doc`), so by default they will not appear in
any versioned document.

#### Recommended: surface neutral endpoints only in a dedicated `default` document

The conservative default — and the pattern the WolverineWebApi sample uses — is to keep a separate
`default` Swagger document for unversioned/neutral endpoints and route them there only.

Note: chains that pass through `UnversionedPolicy.PassThrough` (no `[ApiVersion]`, not `[ApiVersionNeutral]`)
also land in `default` since they share the null `GroupName` with neutral chains, so the predicate below
treats both alike:

```csharp
builder.Services.AddSwaggerGen(x =>
{
    x.SwaggerDoc("default", new OpenApiInfo { Title = "Internal & Neutral", Version = "default" });
    x.SwaggerDoc("v1", new OpenApiInfo { Title = "API v1", Version = "v1" });
    x.SwaggerDoc("v2", new OpenApiInfo { Title = "API v2", Version = "v2" });

    // Neutral chains have no GroupName — show them only in the "default" document.
    x.DocInclusionPredicate((docName, api) =>
        (docName == "default" && api.GroupName is null) || api.GroupName == docName);

    x.OperationFilter<WolverineApiVersioningSwaggerOperationFilter>();
});
```

This keeps `/swagger/v1` and `/swagger/v2` clean: only endpoints that explicitly belong to a public
version appear there. Health checks, webhook receivers, internal admin endpoints, and other neutral
chains stay out of the consumer-facing contract.

#### Alternative: include neutral endpoints in every versioned document

If a neutral endpoint really is part of every public version's contract (a global ping or `/whoami`
endpoint, for example), broaden the predicate to include neutral chains in every doc as well:

```csharp
x.DocInclusionPredicate((docName, api) =>
    api.GroupName is null || api.GroupName == docName);
```

Trade-off: every neutral endpoint will now appear in **every** versioned document. That is rarely what
you want for things like webhook receivers or internal-only endpoints, since publishing them in `v1` and
`v2` invites consumers to call them under those versions and creates an implicit compatibility promise
you did not intend to make. Prefer the `default`-only pattern unless you are certain the endpoint is a
true cross-version concern.

#### Combining the two

You can mix both: surface most neutral endpoints in `default` only, and use a per-endpoint convention
(for instance, an attribute or a marker tag) to selectively include the genuinely cross-version ones in
every document. The `DocInclusionPredicate` receives the full `ApiDescription`, so you can branch on
metadata you attach yourself.

## Sunset and Deprecation Policies

Beyond the attribute-driven `Deprecated = true` flag, you can configure per-version sunset and deprecation
policies with dates and RFC 8288 link references directly in the options:

```csharp
opts.UseApiVersioning(v =>
{
    // Announce that v1.0 is deprecated as of a specific date with a migration link.
    v.Deprecate("1.0")
        .On(DateTimeOffset.Parse("2026-12-31T00:00:00Z"))
        .WithLink(new Uri("https://example.com/migration-guide"));

    // Announce that v3.0 will sunset (be removed) on a future date.
    v.Sunset("3.0")
        .On(DateTimeOffset.Parse("2027-01-01T00:00:00Z"))
        .WithLink(new Uri("https://example.com/migrate-from-v3"), "Migration guide", "text/html");
});
```

### Response headers

When a versioned endpoint is matched, Wolverine emits response headers according to the applicable RFCs:

| Header | RFC | When emitted |
|---|---|---|
| `api-supported-versions` | — | All versioned endpoints (toggleable via `EmitApiSupportedVersionsHeader`) |
| `Deprecation` | RFC 9745 | Endpoints with a deprecation policy |
| `Sunset` | RFC 8594 | Endpoints with a sunset date configured |
| `Link` | RFC 8288 | Endpoints with link references on either policy |

Headers are emitted on **every framework-produced response**, regardless of HTTP status code. That
includes:

- 2xx success responses
- 4xx responses returned via `Results.NotFound()`, `Results.Unauthorized()`, etc.
- 400 validation `ProblemDetails` responses produced by FluentValidation or DataAnnotations middleware
- Middleware short-circuits that return an `IResult` (e.g. an authentication guard returning `Results.Unauthorized()`)

Internally, Wolverine registers the headers via `HttpResponse.OnStarting(...)` from the very first
frame of the chain's middleware list, so emission is correct even when a downstream frame returns
out of the generated handler before the success path completes.

::: warning Exception path is out of scope
Responses produced by the global ASP.NET Core exception handler (e.g. an unhandled exception caught
by `UseExceptionHandler` or `UseDeveloperExceptionPage`) bypass the chain's pipeline entirely, so the
versioning headers are **not** emitted on those responses. If you need them on 5xx, attach a small
ASP.NET Core middleware on the exception path that reads the matched endpoint's
`ApiVersionEndpointHeaderState` metadata and delegates header emission to the registered
`ApiVersionHeaderWriter` so the source of truth (RFC formatting, options toggles) stays in one place:

```csharp
using Microsoft.Extensions.DependencyInjection;

app.Use(async (ctx, next) =>
{
    ctx.Response.OnStarting(static state =>
    {
        var c = (HttpContext)state;
        if (c.Response.StatusCode < 500) return Task.CompletedTask;

        var endpointState = c.GetEndpoint()?.Metadata.GetMetadata<ApiVersionEndpointHeaderState>();
        if (endpointState is null) return Task.CompletedTask;

        var writer = c.RequestServices.GetRequiredService<ApiVersionHeaderWriter>();
        writer.WriteVersioningHeadersTo(c, endpointState);
        return Task.CompletedTask;
    }, ctx);
    await next();
});
```
:::

Toggle the headers globally:

```csharp
opts.UseApiVersioning(v =>
{
    v.EmitApiSupportedVersionsHeader = false; // suppress the supported-versions header
    v.EmitDeprecationHeaders = false;          // suppress Deprecation/Sunset/Link headers
});
```

## OpenAPI Integration

Wolverine attaches two pieces of metadata to every versioned chain during bootstrapping:

- `IEndpointGroupNameMetadata` — the document name string (e.g. `"v1"`, `"v2"`) used by Swashbuckle, Scalar,
  and Microsoft.AspNetCore.OpenApi to partition the OpenAPI output.
- `Asp.Versioning.ApiVersionMetadata` — the typed version model consumed by `Asp.Versioning.Http` tooling if
  it is also present in the application.

### Customising the document name strategy

The default strategy maps `ApiVersion(2, 0)` → `"v2"`. Override it via:

```csharp
opts.UseApiVersioning(v =>
{
    // Use "api-v2" as the Swashbuckle document name for version 2.x
    v.OpenApi.DocumentNameStrategy = apiVersion =>
        apiVersion.MajorVersion.HasValue
            ? $"api-v{apiVersion.MajorVersion}"
            : apiVersion.ToString();
});
```

### Swashbuckle multi-document setup

Register one `SwaggerDoc` per version. Use `DocInclusionPredicate` to route each endpoint to the correct document
via the group name Wolverine has set. Register `WolverineApiVersioningSwaggerOperationFilter` to surface sunset
and deprecation state in the OpenAPI output:

```csharp
builder.Services.AddSwaggerGen(x =>
{
    x.SwaggerDoc("v1", new OpenApiInfo { Title = "API v1", Version = "v1" });
    x.SwaggerDoc("v2", new OpenApiInfo { Title = "API v2", Version = "v2" });

    // Route each endpoint to the document whose name matches the endpoint group name.
    x.DocInclusionPredicate((doc, api) => api.GroupName == doc);

    // Surfaces deprecation / sunset state in the OpenAPI output.
    x.OperationFilter<WolverineApiVersioningSwaggerOperationFilter>();
});
```

`WolverineApiVersioningSwaggerOperationFilter` is **not distributed as a NuGet library**. Copy it from the sample
app (`src/Http/WolverineWebApi/ApiVersioning/WolverineApiVersioningSwaggerOperationFilter.cs`) into your own
project and register it as shown above.

### SwaggerUI version dropdown

Use `DescribeWolverineApiVersions()` to enumerate the discovered versions and wire each into the SwaggerUI
endpoint list:

```csharp
app.UseSwagger();
app.UseSwaggerUI(c =>
{
    // Wire in non-versioned endpoints under a "default" document if you have them.
    c.SwaggerEndpoint("/swagger/default/swagger.json", "default");

    foreach (var description in app.DescribeWolverineApiVersions())
    {
        c.SwaggerEndpoint(
            $"/swagger/{description.DocumentName}/swagger.json",
            description.DisplayName);
    }
});
```

`DescribeWolverineApiVersions()` returns an empty list when versioning is not configured or no versioned
endpoints have been discovered, so the loop is always safe to call.

### Scalar integration

Scalar follows the same pattern. Register one document per version, then iterate `DescribeWolverineApiVersions()`:

```csharp
app.MapScalarApiReference(opts =>
{
    opts.Title = "My API";
    foreach (var description in app.DescribeWolverineApiVersions())
        opts.AddDocument(description.DocumentName, description.DisplayName);
});
```

### Microsoft.AspNetCore.OpenApi

When using the built-in `Microsoft.AspNetCore.OpenApi` package, Wolverine's group name is surfaced as
`ApiDescription.GroupName` in `ApiDescriptionGroupCollection`. The standard ASP.NET Core document filter
mechanism can be used to partition endpoints by group name in exactly the same way as Swashbuckle's
`DocInclusionPredicate`.

## Troubleshooting

**`UrlSegmentPrefix` is set but the route is unchanged.**
Check that the prefix string contains the literal `{version}` token. If no `{version}` is present and there are
versioned endpoints, Wolverine throws at startup:

> `WolverineApiVersioningOptions.UrlSegmentPrefix is set to 'myprefix' which does not contain the required '{version}' token. ... Set UrlSegmentPrefix to null to disable URL-segment versioning, or include '{version}' in the prefix template.`

**All my existing endpoints stopped resolving after I turned on `RequireExplicit`.**
`RequireExplicit` means every endpoint, including infrastructure or health-check endpoints discovered by Wolverine's
assembly scan, must carry `[ApiVersion]`. Switch to `PassThrough` (the default) for any endpoints that should
remain unversioned, or add the attribute. The exception message lists the offending endpoint by its display name.

**Multiple `[ApiVersion]` attributes on the same method.**
Only the first attribute is used; subsequent attributes are silently ignored. If you need to expose a resource at
two different versions, create separate endpoint classes — one per version — sharing the same route template. The
duplicate-detection step in `ApiVersioningPolicy` will catch any `(verb, route, version)` triple that appears
more than once and throw a descriptive `InvalidOperationException` at startup.
