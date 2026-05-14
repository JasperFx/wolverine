# JSON Serialization

::: warning
At this point WolverineFx.Http **only** supports `System.Text.Json` as the default for the HTTP endpoints,
with the JSON settings coming from the application's Minimal API configuration.
:::

::: tip
You can tell Wolverine to ignore all return values as the request body by decorating either the endpoint
method or the whole endpoint class with `[EmptyResponse]`
:::

As explained up above, the "request" type to a Wolverine endpoint is the first argument that is:

1. Concrete
2. Not one of the value types that Wolverine considers for route or query string values
3. *Not* marked with `[FromServices]` from ASP.Net Core

If a parameter like this exists, that will be the request type, and will come
at runtime from deserializing the HTTP request body as JSON.

Likewise, any resource type besides strings will be written to the HTTP response body
as serialized JSON.

In this sample endpoint, both the request and resource types are dealt with by
JSON serialization. Here's the test from the actual Wolverine codebase:

<!-- snippet: sample_post_json_happy_path -->
<a id='snippet-sample_post_json_happy_path'></a>
```cs
[Fact]
public async Task post_json_happy_path()
{
    // This test is using Alba to run an end to end HTTP request
    // and interrogate the results
    var response = await Scenario(x =>
    {
        x.Post.Json(new Question { One = 3, Two = 4 }).ToUrl("/question");
        x.WithRequestHeader("accept", "application/json");
    });

    var result = await response.ReadAsJsonAsync<ArithmeticResults>();

    result.Product.ShouldBe(12);
    result.Sum.ShouldBe(7);
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/Wolverine.Http.Tests/posting_json.cs#L12-L30' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_post_json_happy_path' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Configuring System.Text.Json

Wolverine depends on the value of the `IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>` value registered in your application container for System.Text.Json
configuration. 

But, because there are multiple `JsonOption` types in the AspNetCore world and it's way too easy to pick the wrong one
and get confused and angry about why your configuration isn't impacting Wolverine, there's this extension method helper
that will do the right thing behind the scenes:

<!-- snippet: sample_configuring_stj_for_wolverine -->
<a id='snippet-sample_configuring_stj_for_wolverine'></a>
```cs
var builder = WebApplication.CreateBuilder();

builder.Host.UseWolverine();

builder.Services.ConfigureSystemTextJsonForWolverineOrMinimalApi(o =>
{
    // Do whatever you want here to customize the JSON
    // serialization
    o.SerializerOptions.WriteIndented = true;
});

var app = builder.Build();

app.MapWolverineEndpoints();

return await app.RunJasperFxCommands(args);
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/Wolverine.Http.Tests/Samples/ConfiguringJson.cs#L10-L28' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_configuring_stj_for_wolverine' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Using Newtonsoft.Json

::: tip
Newtonsoft.Json is still much more battle hardened than System.Text.Json, and you may need
to drop back to Newtonsoft.Json for various scenarios. This feature was added specifically
at the request of F# developers.
:::

::: tip
As of **Wolverine 6.0**, Newtonsoft.Json support for Wolverine.Http lives in a separate `WolverineFx.Http.Newtonsoft` NuGet package. The core `WolverineFx.Http` package no longer depends on Newtonsoft.Json; install `WolverineFx.Http.Newtonsoft` alongside it to opt in. This mirrors the symmetric core extraction in `WolverineFx.Newtonsoft` (see [the migration guide](/guide/migration.html#wolverine-http-newtonsoft-moved-to-wolverinefx-http-newtonsoft-package-breaking)).
:::

To install:

```bash
dotnet add package WolverineFx.Http.Newtonsoft
```

The `WolverineFx.Http.Newtonsoft` package provides the same `UseNewtonsoftJsonForSerialization`
API as Wolverine 5.x, now exposed as an extension method on `WolverineHttpOptions`. Add the
`using Wolverine.Http.Newtonsoft;` directive to bring the extensions into scope, and register
the package's services on the `IServiceCollection` alongside `AddWolverineHttp()`:

<!-- snippet: sample_use_newtonsoft_for_http_serialization -->
<a id='snippet-sample_use_newtonsoft_for_http_serialization'></a>
```cs
var builder = WebApplication.CreateBuilder([]);
builder.Services.AddScoped<IUserService, UserService>();

builder.Services.AddMarten(Servers.PostgresConnectionString)
    .IntegrateWithWolverine();

builder.Host.UseWolverine(opts =>
{
    opts.Discovery.IncludeAssembly(GetType().Assembly);
});

builder.Services.AddWolverineHttp();
// As of Wolverine 6.0, Newtonsoft.Json HTTP support lives in the
// separate WolverineFx.Http.Newtonsoft package — register its
// services here, then opt in via UseNewtonsoftJsonForSerialization()
// below.
builder.Services.AddWolverineHttpNewtonsoft();

await using var host = await AlbaHost.For(builder, app =>
{
    app.MapWolverineEndpoints(opts =>
    {
        // Opt into using Newtonsoft.Json for JSON serialization just with Wolverine.HTTP routes
        // Configuring the JSON serialization is optional. This extension method comes from
        // the WolverineFx.Http.Newtonsoft package (using Wolverine.Http.Newtonsoft;).
        opts.UseNewtonsoftJsonForSerialization(settings => settings.TypeNameHandling = TypeNameHandling.All);
    });
});
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/Wolverine.Http.Tests/using_newtonsoft_for_serialization.cs#L19-L51' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_use_newtonsoft_for_http_serialization' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->
