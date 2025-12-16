# Caching <Badge type="tip" text="5.9" />

For caching HTTP responses, Wolverine can simply work with the [Response Caching Middleware in ASP.NET Core](https://learn.microsoft.com/en-us/aspnet/core/performance/caching/middleware?view=aspnetcore-10.0).

Wolverine.HTTP *will* respect your usage of the `[ResponseCache]` attribute on either the endpoint handler or method to write out both the `vary` and `cache-control` HTTP headers --
with an attribute on the method taking precedence. 

Here's an example or two:

<!-- snippet: sample_using_response_cache_attribute -->
<a id='snippet-sample_using_response_cache_attribute'></a>
```cs
// This is all it takes:
[WolverineGet("/cache/one"), ResponseCache(Duration = 3, VaryByHeader = "accept-encoding", NoStore = false)]
public static string GetOne()
{
    return "one";
}

[WolverineGet("/cache/two"), ResponseCache(Duration = 10, NoStore = true)]
public static string GetTwo()
{
    return "two";
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/CachedEndpoint.cs#L8-L23' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_response_cache_attribute' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Wolverine.HTTP will also modify the OpenAPI metadata to reflect the caching as well as embed the metadata in the ASP.Net Core 
`Endpoint` for utilization inside of the response caching middleware.
