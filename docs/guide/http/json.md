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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/Wolverine.Http.Tests/posting_json.cs#L12-L31' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_post_json_happy_path' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/Wolverine.Http.Tests/Samples/ConfiguringJson.cs#L10-L29' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_configuring_stj_for_wolverine' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Using Newtonsoft.Json

::: tip
Newtonsoft.Json is still much more battle hardened than System.Text.Json, and you may need
to drop back to Newtonsoft.Json for various scenarios. This feature was added specifically
at the request of F# developers.
:::

To opt into using Newtonsoft.Json for the JSON serialization of *HTTP endpoints*, you have this option within the call
to the `MapWolverineEndpoints()` configuration:

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

await using var host = await AlbaHost.For(builder, app =>
{
    app.MapWolverineEndpoints(opts =>
    {
        // Opt into using Newtonsoft.Json for JSON serialization just with Wolverine.HTTP routes
        // Configuring the JSON serialization is optional
        opts.UseNewtonsoftJsonForSerialization(settings => settings.TypeNameHandling = TypeNameHandling.All);
    });
});
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/Wolverine.Http.Tests/using_newtonsoft_for_serialization.cs#L18-L43' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_use_newtonsoft_for_http_serialization' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->
