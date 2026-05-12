# Output Caching <Badge type="tip" text="5.13" />

Wolverine.HTTP supports ASP.NET Core's [Output Caching](https://learn.microsoft.com/en-us/aspnet/core/performance/caching/output?view=aspnetcore-10.0) middleware for server-side response caching. This is different from the client-side [Response Caching](/guide/http/caching) that uses `Cache-Control` headers.

## Response Caching vs. Output Caching

| Feature | Response Caching (`[ResponseCache]`) | Output Caching (`[OutputCache]`) |
|---------|--------------------------------------|----------------------------------|
| Where cached | Client-side (browser) | Server-side (in-memory) |
| Mechanism | Sets `Cache-Control` headers | Stores responses in server memory |
| Cache invalidation | Based on headers/expiration | Programmatic via tags or expiration |
| Use case | Reduce bandwidth, leverage browser cache | Reduce server processing, protect downstream services |

For client-side response caching with `[ResponseCache]`, see [Caching](/guide/http/caching).

## Setup

First, register the output caching services and configure your caching policies:

<!-- snippet: sample_adding_output_cache_services -->
<a id='snippet-sample_adding_output_cache_services'></a>
```cs
builder.Services.AddOutputCache(options =>
{
    options.AddPolicy("short", builder => builder.Expire(TimeSpan.FromSeconds(5)));
});

builder.Services.AddRateLimiter(options =>
{
    options.AddFixedWindowLimiter("fixed", opt =>
    {
        opt.PermitLimit = 1;
        opt.Window = TimeSpan.FromSeconds(10);
        opt.QueueLimit = 0;
    });
    options.RejectionStatusCode = 429;
});
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Program.cs#L84-L103' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_adding_output_cache_services' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Then add the output caching middleware to your pipeline. It should be placed **after** routing and authorization, but **before** `MapWolverineEndpoints()`:

<!-- snippet: sample_using_output_cache_middleware -->
<a id='snippet-sample_using_output_cache_middleware'></a>
```cs
app.UseOutputCache();

app.UseRateLimiter();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Program.cs#L265-L272' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_output_cache_middleware' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Per-Endpoint Usage

Apply the `[OutputCache]` attribute to any Wolverine.HTTP endpoint to enable output caching. This works exactly as it does in standard ASP.NET Core -- no special Wolverine infrastructure is needed.

### Using a Named Policy

<!-- snippet: sample_output_cache_endpoint -->
<a id='snippet-sample_output_cache_endpoint'></a>
```cs
[WolverineGet("/api/cached")]
[OutputCache(PolicyName = "short")]
public static string GetCached()
{
    return $"Cached at {DateTime.UtcNow:O} - {Interlocked.Increment(ref _counter)}";
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Caching/OutputCacheEndpoints.cs#L10-L18' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_output_cache_endpoint' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

### Using the Default (Base) Policy

<!-- snippet: sample_output_cache_default_endpoint -->
<a id='snippet-sample_output_cache_default_endpoint'></a>
```cs
[WolverineGet("/api/cached-default")]
[OutputCache]
public static string GetCachedDefault()
{
    return $"Default cached at {DateTime.UtcNow:O} - {Interlocked.Increment(ref _counter)}";
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Caching/OutputCacheEndpoints.cs#L20-L28' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_output_cache_default_endpoint' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Configuring Policies

Output cache policies are configured through `AddOutputCache()` in your service registration. Common options include:

### Expiration

```cs
builder.Services.AddOutputCache(options =>
{
    // Base policy applied to all [OutputCache] endpoints without a named policy
    options.AddBasePolicy(builder => builder.Expire(TimeSpan.FromSeconds(30)));

    // Named policy with a shorter expiration
    options.AddPolicy("short", builder => builder.Expire(TimeSpan.FromSeconds(5)));
});
```

### VaryByQuery and VaryByHeader

```cs
builder.Services.AddOutputCache(options =>
{
    options.AddPolicy("vary-by-query", builder =>
        builder.SetVaryByQuery("page", "pageSize"));

    options.AddPolicy("vary-by-header", builder =>
        builder.SetVaryByHeader("Accept-Language"));
});
```

### Tag-Based Cache Invalidation

You can tag cached responses and then evict them programmatically:

```cs
builder.Services.AddOutputCache(options =>
{
    options.AddPolicy("tagged", builder =>
        builder.Tag("products").Expire(TimeSpan.FromMinutes(10)));
});
```

Then invalidate by tag when data changes:

```cs
public static async Task Handle(ProductUpdated message, IOutputCacheStore cacheStore)
{
    await cacheStore.EvictByTagAsync("products", default);
}
```

## Important Notes

- Output caching is a standard ASP.NET Core feature. Wolverine.HTTP endpoints are standard ASP.NET Core endpoints, so the `[OutputCache]` attribute works with no additional Wolverine-specific setup.
- The `UseOutputCache()` middleware must be added to the pipeline for the attribute to take effect.
- Output caching only applies to GET and HEAD requests by default.
- Authenticated requests (those with an `Authorization` header) are not cached by default.

For the full set of configuration options, see [Microsoft's Output Caching documentation](https://learn.microsoft.com/en-us/aspnet/core/performance/caching/output?view=aspnetcore-10.0).
