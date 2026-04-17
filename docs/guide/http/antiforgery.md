# Antiforgery / CSRF Protection

Wolverine.HTTP integrates with ASP.NET Core's built-in [antiforgery middleware](https://learn.microsoft.com/en-us/aspnet/core/security/anti-request-forgery) to protect form-based endpoints from Cross-Site Request Forgery (CSRF) attacks.

## How It Works

When a Wolverine HTTP endpoint uses form data binding (via `[FromForm]` or file uploads), Wolverine automatically adds `IAntiforgeryMetadata` to the endpoint's metadata with `RequiresValidation = true`. ASP.NET Core's antiforgery middleware then validates the antiforgery token on incoming requests to those endpoints.

## Setup

To enable antiforgery protection, register the antiforgery services and middleware in your ASP.NET Core application:

```csharp
var builder = WebApplication.CreateBuilder(args);

// Add antiforgery services
builder.Services.AddAntiforgery();

var app = builder.Build();

// Add the antiforgery middleware BEFORE routing
app.UseAntiforgery();

app.MapWolverineEndpoints();
app.Run();
```

## Automatic Protection for Form Endpoints

Any Wolverine HTTP endpoint that binds form data automatically requires antiforgery token validation. No additional configuration is needed:

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
| `[FromForm]` parameter | Antiforgery required automatically |
| File upload (`IFormFile`) | Antiforgery required automatically |
| JSON body (no form data) | No antiforgery by default |
| `[DisableAntiforgery]` on method or class | Antiforgery explicitly disabled |
| `[ValidateAntiforgery]` on method or class | Antiforgery explicitly required |
| `RequireAntiforgeryOnAll()` | Antiforgery required on all endpoints |

::: info
This feature relies entirely on ASP.NET Core's built-in antiforgery infrastructure. Wolverine simply sets the appropriate `IAntiforgeryMetadata` on endpoints so the middleware knows which endpoints to protect. No additional NuGet packages are required.
:::
