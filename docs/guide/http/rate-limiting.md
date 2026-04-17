# Rate Limiting

Wolverine.HTTP endpoints are standard ASP.NET Core endpoints, so ASP.NET Core's [built-in rate limiting middleware](https://learn.microsoft.com/en-us/aspnet/core/performance/rate-limit) works directly with no additional Wolverine-specific setup required.

## Setup

First, register the rate limiting services and define one or more rate limiting policies in your application's service configuration:

<!-- snippet: sample_rate_limiting_configuration -->
<a id='snippet-sample_rate_limiting_configuration'></a>
```cs
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Program.cs#L76-L87' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_rate_limiting_configuration' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Then add the rate limiting middleware to the request pipeline. This must be placed **before** `MapWolverineEndpoints()`:

<!-- snippet: sample_use_rate_limiter_middleware -->
<a id='snippet-sample_use_rate_limiter_middleware'></a>
```cs
app.UseRateLimiter();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Program.cs#L217-L219' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_use_rate_limiter_middleware' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Per-Endpoint Rate Limiting

Apply the `[EnableRateLimiting]` attribute to individual Wolverine.HTTP endpoints to enforce a named rate limiting policy:

<!-- snippet: sample_rate_limited_endpoint -->
<a id='snippet-sample_rate_limited_endpoint'></a>
```cs
[WolverineGet("/api/rate-limited")]
[EnableRateLimiting("fixed")]
public static string GetRateLimited()
{
    return "OK";
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/RateLimiting/RateLimitedEndpoints.cs#L8-L15' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_rate_limited_endpoint' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

When the rate limit is exceeded, the middleware returns a `429 Too Many Requests` response automatically.

## Disabling Rate Limiting

If you have a global rate limiting policy applied, you can opt specific endpoints out using the `[DisableRateLimiting]` attribute:

```cs
[WolverineGet("/api/health")]
[DisableRateLimiting]
public static string HealthCheck()
{
    return "Healthy";
}
```

## Global Rate Limiting

You can apply a global rate limiting policy that applies to all endpoints by using a global limiter instead of (or in addition to) named policies:

```cs
builder.Services.AddRateLimiter(options =>
{
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 100,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));
    options.RejectionStatusCode = 429;
});
```

## Applying Rate Limiting via ConfigureEndpoints

You can use Wolverine's `ConfigureEndpoints()` to apply rate limiting metadata to all Wolverine.HTTP endpoints programmatically:

```cs
app.MapWolverineEndpoints(opts =>
{
    opts.ConfigureEndpoints(httpChain =>
    {
        // Apply rate limiting to all Wolverine endpoints
        httpChain.WithMetadata(new EnableRateLimitingAttribute("fixed"));
    });
});
```

## Available Rate Limiting Algorithms

ASP.NET Core provides several built-in rate limiting algorithms:

- **Fixed window** -- limits requests in fixed time intervals
- **Sliding window** -- uses a sliding time window for smoother rate limiting
- **Token bucket** -- allows bursts of traffic up to a configured limit
- **Concurrency** -- limits the number of concurrent requests

See the [ASP.NET Core rate limiting documentation](https://learn.microsoft.com/en-us/aspnet/core/performance/rate-limit) for full details on each algorithm and advanced configuration options.
