# Content Negotiation

Wolverine supports content negotiation for HTTP endpoints, allowing you to serve different response formats based on the client's `Accept` header. This is done through the `[Writes]` attribute and the `WriteResponse` naming convention.

## Response Content Negotiation

To add content negotiation support for responses, add methods with the `[Writes("content-type")]` attribute to your endpoint class. These methods will be called instead of the default JSON serialization when the client's `Accept` header matches:

<!-- snippet: sample_conneg_write_response -->
<a id='snippet-sample_conneg_write_response'></a>
```cs
/// <summary>
/// Demonstrates content negotiation with [Writes] attribute.
/// Multiple WriteResponse methods handle different content types.
/// </summary>
public static class ConnegWriteEndpoints
{
    [WolverineGet("/conneg/write")]
    public static ConnegItem GetItem()
    {
        return new ConnegItem("Widget", 42);
    }

    /// <summary>
    /// Writes the response as plain text when Accept: text/plain
    /// </summary>
    [Writes("text/plain")]
    public static Task WriteResponse(HttpContext context, ConnegItem response)
    {
        context.Response.ContentType = "text/plain";
        return context.Response.WriteAsync($"{response.Name}: {response.Value}");
    }

    /// <summary>
    /// Writes the response as CSV when Accept: text/csv
    /// </summary>
    [Writes("text/csv")]
    public static Task WriteResponseCsv(HttpContext context, ConnegItem response)
    {
        context.Response.ContentType = "text/csv";
        return context.Response.WriteAsync($"Name,Value\n{response.Name},{response.Value}");
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/ConnegEndpoints.cs#L11-L52' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_conneg_write_response' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Key points:
- Methods must have the `[Writes("content-type")]` attribute to specify which content type they handle
- The method name can be anything — it's the `[Writes]` attribute that matters
- Methods receive the `HttpContext` and the response resource as parameters
- Methods can be sync (`void`) or async (`Task`)
- Multiple `[Writes]` methods can coexist on the same class for different content types

## Loose vs Strict Mode

By default, content negotiation uses **Loose** mode: if no `[Writes]` method matches the client's `Accept` header, Wolverine falls back to JSON serialization. This ensures clients always get a response.

### Strict Mode

Use `[StrictConneg]` to enable **Strict** mode. In strict mode, if no `[Writes]` method matches, Wolverine returns HTTP 406 Not Acceptable:

<!-- snippet: sample_conneg_strict -->
<a id='snippet-sample_conneg_strict'></a>
```cs
/// <summary>
/// Strict content negotiation — returns 406 when Accept header doesn't match
/// </summary>
[StrictConneg]
public static class StrictConnegEndpoints
{
    [WolverineGet("/conneg/strict")]
    public static ConnegItem GetStrictItem()
    {
        return new ConnegItem("StrictWidget", 99);
    }

    [Writes("text/plain")]
    public static Task WriteResponse(HttpContext context, ConnegItem response)
    {
        context.Response.ContentType = "text/plain";
        return context.Response.WriteAsync($"{response.Name}: {response.Value}");
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/ConnegEndpoints.cs#L54-L76' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_conneg_strict' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

### Global Configuration

You can set the default `ConnegMode` for all endpoints using a policy:

```csharp
app.MapWolverineEndpoints(opts =>
{
    // Set strict mode globally
    opts.ConfigureEndpoints(chain =>
    {
        chain.ConnegMode = ConnegMode.Strict;
    });
});
```

Per-endpoint configuration (via `[StrictConneg]` attribute) overrides the global setting.

## Loose Mode Fallback

In the default **Loose** mode, when no `[Writes]` method matches the `Accept` header, the response falls back to JSON serialization — exactly the same as an endpoint without any content negotiation:

<!-- snippet: sample_conneg_loose_fallback -->
<a id='snippet-sample_conneg_loose_fallback'></a>
```cs
/// <summary>
/// Loose content negotiation (default) — falls back to JSON when no match
/// </summary>
public static class LooseConnegEndpoints
{
    [WolverineGet("/conneg/loose")]
    public static ConnegItem GetLooseItem()
    {
        return new ConnegItem("LooseWidget", 77);
    }

    [Writes("text/plain")]
    public static Task WriteResponse(HttpContext context, ConnegItem response)
    {
        context.Response.ContentType = "text/plain";
        return context.Response.WriteAsync($"{response.Name}: {response.Value}");
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/ConnegEndpoints.cs#L78-L99' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_conneg_loose_fallback' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## How It Works

At code generation time, Wolverine generates branching code that checks the `Accept` header and dispatches to the correct writer:

```csharp
// Generated code (simplified)
var acceptHeader = httpContext.Request.Headers.Accept.ToString();

if (acceptHeader.Contains("text/plain"))
{
    httpContext.Response.ContentType = "text/plain";
    await ConnegWriteEndpoints.WriteResponse(httpContext, item_response);
}
else if (acceptHeader.Contains("text/csv"))
{
    httpContext.Response.ContentType = "text/csv";
    await ConnegWriteEndpoints.WriteResponseCsv(httpContext, item_response);
}
else
{
    // Loose: fallback to JSON
    await WriteJsonAsync(httpContext, item_response);
    // Strict: httpContext.Response.StatusCode = 406;
}
```

This means there is **zero runtime reflection** — the content type dispatching is compiled at startup.

## Request Content Type Negotiation

For request-side content negotiation (dispatching based on `Content-Type` header), use the existing `[AcceptsContentType]` attribute to create separate endpoint methods for different request formats:

```csharp
public static class RequestConnegEndpoints
{
    [WolverinePost("/items"), AcceptsContentType("application/vnd.item.v1+json")]
    public static ItemCreated CreateV1(CreateItemV1 command)
    {
        return new ItemCreated(command.Name, "v1");
    }

    [WolverinePost("/items"), AcceptsContentType("application/vnd.item.v2+json")]
    public static ItemCreated CreateV2(CreateItemV2 command)
    {
        return new ItemCreated(command.Name, "v2");
    }
}
```

This allows the same route and HTTP method to handle different request body formats.
