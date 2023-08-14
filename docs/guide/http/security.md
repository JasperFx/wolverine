# Authentication and Authorization

Wolverine.HTTP endpoints are just routes within your ASP.Net Core application, and will happily work with all existing
ASP.Net Core middleware. Likewise, the built int `[AllowAnonymous]` and `[Authorize]` attributes from ASP.Net Core are
valid on Wolverine HTTP endpoints.

To require authorization on all endpoints (which is overridden by `[AllowAnonymous]`), use this syntax:

```csharp
app.MapWolverineEndpoints(opts =>
{
    opts.RequireAuthorizeOnAll();
});
```

or more selectively, the code above is just syntactical sugar for:

<!-- snippet: sample_RequireAuthorizeOnAll -->
<a id='snippet-sample_requireauthorizeonall'></a>
```cs
/// <summary>
/// Equivalent of calling RequireAuthorization() on all wolverine endpoints
/// </summary>
public void RequireAuthorizeOnAll()
{
    ConfigureEndpoints(e => e.RequireAuthorization());
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/Wolverine.Http/WolverineHttpOptions.cs#L74-L84' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_requireauthorizeonall' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->
