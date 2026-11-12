# Antiforgery / CSRF Protection

Wolverine.HTTP integrates with ASP.NET Core's built-in [antiforgery middleware](https://learn.microsoft.com/en-us/aspnet/core/security/anti-request-forgery) to protect form-based endpoints from Cross-Site Request Forgery (CSRF) attacks.

## How It Works

Wolverine decides per endpoint whether antiforgery validation is required and sets `IAntiforgeryMetadata` on the endpoint
accordingly. ASP.NET Core's antiforgery middleware records its verdict for the request, and the code Wolverine generates
for an endpoint that requires validation checks that verdict before it binds anything: a request that failed is answered
with a **400** and ProblemDetails (`"Antiforgery validation failed"`), and the handler never runs.

## .NET 11: on by default for form endpoints

On **.NET 11**, form data and file upload endpoints require antiforgery validation **by default**, as Minimal API's
do. ASP.NET Core 11 injects its own CSRF protection middleware, so this needs no setup and no tokens: same-origin
browser requests (judged by `Sec-Fetch-Site`, or `Origin` for older browsers) and non-browser clients go through, and
cross-site browser requests are refused.

To keep the 6.x behaviour, turn the default off:

```csharp
app.MapWolverineEndpoints(opts =>
{
    // Form endpoints no longer require antiforgery validation unless marked [ValidateAntiforgery]
    opts.DisableAutoAntiforgeryOnFormEndpoints();
});
```

A form endpoint marked with ASP.NET Core 11's `[ShortCircuit]` runs inside the routing middleware, before any
antiforgery middleware, so Wolverine refuses it at startup unless it is also marked `[DisableAntiforgery]`.

## .NET 10: opt in

On **.NET 10** the default stays off: ASP.NET Core 10 throws on every request to an endpoint that requires antiforgery
validation unless the application has the antiforgery middleware, and validates tokens rather than the request's origin.
Opt in with `opts.AutoAntiforgeryOnFormEndpoints()`, and register the services and middleware:

```csharp
var builder = WebApplication.CreateBuilder(args);

// Add antiforgery services
builder.Services.AddAntiforgery();

var app = builder.Build();

// Add the antiforgery middleware BEFORE routing
app.UseAntiforgery();

app.MapWolverineEndpoints(opts => opts.AutoAntiforgeryOnFormEndpoints());
app.Run();
```

Calling `UseAntiforgery()` on .NET 11 as well switches that application to token validation.

## Automatic Protection for Form Endpoints

With the default on (.NET 11) or `AutoAntiforgeryOnFormEndpoints()` (.NET 10), any Wolverine HTTP endpoint that binds form data requires antiforgery validation:

<!-- snippet: sample_antiforgery_form_endpoint -->
<a id='snippet-sample_antiforgery_form_endpoint'></a>
```cs
// Antiforgery validation is automatic for [FromForm] endpoints
[WolverinePost("/api/form/contact")]
public static string SubmitContactForm([FromForm] string name, [FromForm] string email)
{
    return $"Received from {name} ({email})";
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Antiforgery/AntiforgeryEndpoints.cs#L8-L15' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_antiforgery_form_endpoint' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Opting Out with [DisableAntiforgery]

For endpoints that receive form data but should not require antiforgery validation (such as webhook receivers or API endpoints called by external services), use the `[DisableAntiforgery]` attribute:

<!-- snippet: sample_antiforgery_disabled -->
<a id='snippet-sample_antiforgery_disabled'></a>
```cs
// Opt out of antiforgery validation
[DisableAntiforgery]
[WolverinePost("/api/form/webhook")]
public static string WebhookReceiver([FromForm] string payload)
{
    return $"Processed: {payload}";
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Antiforgery/AntiforgeryEndpoints.cs#L17-L25' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_antiforgery_disabled' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The `[DisableAntiforgery]` attribute can also be applied at the class level to disable antiforgery for all endpoints in that class.

## Opting In with [ValidateAntiforgery]

For non-form endpoints that should require antiforgery validation (such as sensitive JSON API endpoints), use the `[ValidateAntiforgery]` attribute:

<!-- snippet: sample_antiforgery_explicit -->
<a id='snippet-sample_antiforgery_explicit'></a>
```cs
// Opt in to antiforgery validation for non-form endpoints
[ValidateAntiforgery]
[WolverinePost("/api/secure/action")]
public static string SecureAction(SecureCommand command)
{
    return $"Executed: {command.Action}";
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Antiforgery/AntiforgeryEndpoints.cs#L27-L35' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_antiforgery_explicit' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Global Configuration

To require antiforgery validation on all Wolverine HTTP endpoints regardless of whether they use form binding:

```csharp
app.MapWolverineEndpoints(opts =>
{
    opts.RequireAntiforgeryOnAll();
});
```

Individual endpoints can still opt out using `[DisableAntiforgery]`.

## Summary

| Scenario | Behavior |
|----------|----------|
| `[FromForm]` parameter | Antiforgery required by default on .NET 11; with `AutoAntiforgeryOnFormEndpoints()` on .NET 10 |
| File upload (`IFormFile`, `MultipartReader`) | Same as `[FromForm]` |
| JSON body (no form data) | No antiforgery by default |
| `[DisableAntiforgery]` on method or class | Antiforgery explicitly disabled |
| `[ValidateAntiforgery]` on method or class | Antiforgery explicitly required |
| `RequireAntiforgeryOnAll()` | Antiforgery required on all endpoints but those marked `[DisableAntiforgery]` |
| `DisableAutoAntiforgeryOnFormEndpoints()` | Form endpoints no longer require it by default |
| A request that fails validation | 400 with ProblemDetails, before the request body is read |

::: info
This feature relies on ASP.NET Core's built-in antiforgery infrastructure. Wolverine sets the appropriate `IAntiforgeryMetadata` on endpoints so the middleware knows which endpoints to protect, and refuses the requests it marks invalid. No additional NuGet packages are required.
:::
