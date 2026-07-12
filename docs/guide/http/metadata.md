# OpenAPI Metadata

As much as possible, Wolverine is trying to glean [OpenAPI](https://www.openapis.org/) ([Swashbuckle](https://learn.microsoft.com/en-us/aspnet/core/tutorials/getting-started-with-swashbuckle?view=aspnetcore-7.0&tabs=visual-studio) / Swagger) metadata from the method signature
of the HTTP endpoint methods instead of forcing developers to add repetitive boilerplate code.

There's a handful of predictable rules about metadata for Wolverine endpoints:

* `application/json` is assumed for any request body type or any response body type
* `text/plain` is the content type for any endpoint that returns a string as the response body
* `200` and `500` are always assumed as valid status codes by default
* `404` is also part of the metadata in most cases

That aside, there's plenty of ways to modify the OpenAPI metadata for Wolverine endpoints for whatever you need. First off,
all the attributes from ASP.Net Core that you use for MVC controller methods happily work on Wolverine endpoints:

<!-- snippet: sample_using_openapi_attributes -->
<a id='snippet-sample_using_openapi_attributes'></a>
```cs
public class SignupEndpoint
{
    // The first couple attributes are ASP.Net Core
    // attributes that add OpenAPI metadata to this endpoint
    [Tags("Users")]
    [ProducesResponseType(204)]
    [WolverinePost("/users/sign-up")]
    public static IResult SignUp(SignUpRequest request)
    {
        return Results.NoContent();
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/SignupEndpoint.cs#L6-L20' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_openapi_attributes' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Or if you prefer the fluent interface from Minimal API, that's actually supported as well for either individual endpoints or by 
policy directly on the `HttpChain` model:

<!-- snippet: sample_programmatic_one_off_openapi_metadata -->
<a id='snippet-sample_programmatic_one_off_openapi_metadata'></a>
```cs
public static void Configure(HttpChain chain)
{
    // This sample is from Wolverine itself on endpoints where all you do is forward
    // a request directly to a Wolverine messaging endpoint for later processing
    chain.Metadata.Add(builder =>
    {
        // Adding metadata
        builder.Metadata.Add(new WolverineProducesResponseTypeMetadata { StatusCode = 202, Type = null });
    });
    // This is run after all other metadata has been applied, even after the wolverine built-in metadata
    // So use this if you want to change or remove some metadata
    chain.Metadata.Finally(builder =>
    {
        builder.RemoveStatusCodeResponse(200);
    });
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/Wolverine.Http/Runtime/PublishingEndpoint.cs#L15-L33' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_programmatic_one_off_openapi_metadata' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

::: tip
For HTTP API versioning that partitions the OpenAPI output into one document per version, see the
[Versioning guide](./versioning.md). It covers the multi-document `SwaggerDoc` setup, `DocInclusionPredicate`,
`DescribeWolverineApiVersions()`, and Scalar integration.
:::

## Swashbuckle and Wolverine

[Swashbuckle](https://github.com/domaindrivendev/Swashbuckle.AspNetCore) is de facto the OpenAPI tooling for ASP.Net Core
applications. It's also very MVC Core-centric in its assumptions about how to generate OpenAPI metadata to describe endpoints.
If you need to (or just want to), you can do quite a bit to control exactly how Swashbuckle works against
Wolverine endpoints by using a custom `IOperationFilter` of your making that can use Wolverine's own `HttpChain` model
for finer grained control. Here's a sample from the Wolverine testing code that just uses Wolverine' own model to
determine the OpenAPI operation id:

<!-- snippet: sample_wolverineoperationfilter -->
<a id='snippet-sample_wolverineoperationfilter'></a>
```cs
// This class is NOT distributed in any kind of Nuget today, but feel very free
// to copy this code into your own as it is at least tested through Wolverine's
// CI test suite
public class WolverineOperationFilter : IOperationFilter // IOperationFilter is from Swashbuckle itself
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        if (context.ApiDescription.ActionDescriptor is WolverineActionDescriptor action)
        {
            operation.OperationId = action.Chain.OperationId;
        }
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/WolverineOperationFilter.cs#L7-L22' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_wolverineoperationfilter' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

And that would be registered with Swashbuckle inside of your `Program.Main()` method like so:

<!-- snippet: sample_register_custom_swashbuckle_filter -->
<a id='snippet-sample_register_custom_swashbuckle_filter'></a>
```cs
builder.Services.AddSwaggerGen(x =>
{
    x.SwaggerDoc("default", new OpenApiInfo { Title = "Wolverine Web API", Version = "default" });
    x.SwaggerDoc("v1", new OpenApiInfo { Title = "Wolverine Web API v1", Version = "v1" });
    x.SwaggerDoc("v2", new OpenApiInfo { Title = "Wolverine Web API v2", Version = "v2" });
    x.SwaggerDoc("v3", new OpenApiInfo { Title = "Wolverine Web API v3", Version = "v3" });
    // v4 has no options.Deprecate("4.0") — used by integration tests to prove the
    // attribute-driven [ApiVersion("4.0", Deprecated = true)] is honoured on its own.
    x.SwaggerDoc("v4", new OpenApiInfo { Title = "Wolverine Web API v4", Version = "v4" });
    x.OperationFilter<WolverineOperationFilter>();
    x.OperationFilter<WolverineApiVersioningSwaggerOperationFilter>();
    x.DocInclusionPredicate((docName, api) =>
        docName == "default" || api.GroupName == docName);
    x.ResolveConflictingActions(apiDescriptions => apiDescriptions.First());
});
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Program.cs#L60-L77' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_register_custom_swashbuckle_filter' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Operation Id

::: warning
You will have to use the custom `WolverineOperationFilter` in the previous section to relay Wolverine's operation id
determination to Swashbuckle. We have not (yet) been able to relay that information to Swashbuckle otherwise.
:::

By default, Wolverine.HTTP is trying to mimic the logic for determining the OpenAPI `operationId` logic from MVC Core which
is *endpoint class name*.*method name*. You can also override the operation id through the normal routing attribute through
an optional property as shown below (from the Wolverine.HTTP test code):

<!-- snippet: sample_override_operation_id_for_openapi -->
<a id='snippet-sample_override_operation_id_for_openapi'></a>
```cs
// Override the operation id within the generated OpenAPI
// metadata
[WolverineGet("/fake/hello/async", OperationId = "OverriddenId")]
public Task<string> SayHelloAsync()
{
    return Task.FromResult("Hello");
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/FakeEndpoint.cs#L13-L22' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_override_operation_id_for_openapi' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## IHttpAware or IEndpointMetadataProvider Models

Wolverine honors the ASP.Net Core [IEndpointMetadataProvider](https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.http.metadata.iendpointmetadataprovider?view=aspnetcore-7.0)
interface on resource types to add or modify endpoint metadata.

If you want Wolverine to automatically apply metadata (and HTTP runtime behavior) based on the resource type of 
an HTTP endpoint, you can have your response type implement the `IHttpAware` interface from Wolverine. As an example, 
consider the `CreationResponse` type in Wolverine:

<!-- snippet: sample_creationresponse -->
<a id='snippet-sample_creationresponse'></a>
```cs
/// <summary>
/// Base class for resource types that denote some kind of resource being created
/// in the system. Wolverine specific, and more efficient, version of Created<T> from ASP.Net Core
/// </summary>
public record CreationResponse([StringSyntax("Route")]string Url) : IHttpAware
{
    public static void PopulateMetadata(MethodInfo method, EndpointBuilder builder)
    {
        builder.RemoveStatusCodeResponse(200);

        var create = new MethodCall(method.DeclaringType!, method).Creates.FirstOrDefault()?.VariableType;
        var metadata = new WolverineProducesResponseTypeMetadata { Type = create, StatusCode = 201 };
        builder.Metadata.Add(metadata);
    }

    void IHttpAware.Apply(HttpContext context)
    {
        context.Response.Headers.Location = Url;
        context.Response.StatusCode = 201;
    }

    public static CreationResponse<T> For<T>(T value, string url) => new CreationResponse<T>(url, value);
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/Wolverine.Http/IHttpAware.cs#L82-L107' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_creationresponse' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Any endpoint that returns `CreationResponse` or a sub class will automatically expose a status code of `201` for successful
processing to denote resource creation instead of the generic `200`. Same goes for the built-in `AcceptResponse` type, but returning `202` status. Your own custom implementations of the `IHttpAware`
interface would apply the metadata declarations at configuration time so that those customizations would be part of the
exported Swashbuckle documentation of the system.

As of Wolverine 3.4, Wolverine will also apply OpenAPI metadata from any value created by compound handler middleware
or other middleware that implements the `IEndpointMetadataProvider` interface -- which many `IResult` implementations
from within ASP.Net Core middleware do. Consider this example from the tests:

<!-- snippet: sample_using_optional_iresult_with_openapi_metadata -->
<a id='snippet-sample_using_optional_iresult_with_openapi_metadata'></a>
```cs
public class ValidatedCompoundEndpoint2
{
    public static User? Load(BlockUser2 cmd)
    {
        return cmd.UserId.IsNotEmpty() ? new User(cmd.UserId) : null;
    }

    // This method would be called, and if the NotFound value is
    // not null, will stop the rest of the processing
    // Likewise, Wolverine will use the NotFound type to add
    // OpenAPI metadata
    public static NotFound? Validate(User? user)
    {
        if (user == null)
            return (NotFound?)Results.NotFound<User>(user);

        return null;
    }

    [WolverineDelete("/optional/result")]
    public static  string Handle(BlockUser2 cmd, User user)
    {
        return "Ok - user blocked";
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Validation/ValidatedCompoundEndpoint.cs#L33-L60' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_optional_iresult_with_openapi_metadata' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Generating the OpenAPI Document at the Command Line

::: tip
**Reach for this whenever Microsoft's built-in OpenAPI generation chokes on your infrastructure.**
The standard build-time generators ([`Microsoft.Extensions.ApiDescription.Server`](#with-microsoft-extensions-apidescription-server)
and NSwag's `GetDocument.Insider`) call `IHost.StartAsync()`, which boots Wolverine's hosted service and
tries to connect to your database and/or message broker *before* a single line of JSON is written — so
they routinely fail in build/CI environments that have no real database or broker. The `openapi` command
was **purposefully designed to avoid that**: it never starts your host, so it never opens a database or
broker connection just to emit a JSON file.
:::

Wolverine.HTTP adds an `openapi` command to the JasperFx command line (the same command line you already
use for `dotnet run -- codegen`, `dotnet run -- check-env`, and friends). It reuses the host your
application already builds — Wolverine added and `MapWolverineEndpoints()` already called — and asks the
registered OpenAPI document provider (the `Microsoft.AspNetCore.OpenApi` service that Microsoft's own
`GetDocument.Insider` tool uses) to serialize the document directly from your endpoint metadata. The
host is never started, so the result is functionally equivalent to Microsoft's output without any of the
startup connectivity.

The only prerequisite is that your application registers the built-in OpenAPI services and maps the
Wolverine endpoints before handing control to the JasperFx command line:

```csharp
builder.Services.AddOpenApi();      // Microsoft.AspNetCore.OpenApi
builder.Services.AddWolverineHttp();

var app = builder.Build();

app.MapWolverineEndpoints();

// The openapi command is dispatched from here
return await app.RunJasperFxCommands(args);
```

Then generate the document:

```bash
# Write the default "v1" document to standard output
dotnet run -- openapi

# Write a specific document to a chosen file path
dotnet run -- openapi --document v1 --output ./artifacts/openapi.json

# List the OpenAPI documents this application exposes
dotnet run -- openapi --list
```

### Inspecting a single route

When you just want to look at the OpenAPI metadata for one endpoint — a great way to troubleshoot how
Wolverine.HTTP is binding parameters, negotiating content, or shaping responses — use `--route`. It does
a case-insensitive fuzzy match against the route templates (so it may match several related routes) and
emits a document containing only those paths plus the schema components they reference:

```bash
# Only the routes whose template contains "todoitems", with their schemas
dotnet run -- openapi --route /todoitems --output ./todoitems.json

# Fuzzy match can return multiple related routes
dotnet run -- openapi -r todoitems
```

| Flag | Alias | Description |
| --- | --- | --- |
| `--document` | `-d` | The named document to generate. Defaults to `v1`, the default document name registered by `AddOpenApi()`. |
| `--output` | `-o` | File path for the generated JSON. When omitted (or set to `-`), the document is written to standard output. Use `--output` to capture a clean JSON file, since application and command-line logging is also written to the console. |
| `--route` | `-r` | Fuzzy (case-insensitive, substring) filter on the route template. Only matching paths — and the schema components they reference — are written. |
| `--list` | `-l` | List the document names this application exposes and exit. |

::: warning
Because the host is never started, the document reflects exactly the endpoints discovered at
`MapWolverineEndpoints()` time. Endpoints that are only added during host startup (for example, by an
asynchronous Wolverine extension) will not appear. This matches the goal of build-time generation, but
is worth knowing if you add endpoints dynamically.
:::

The same holds in-process: Wolverine's endpoint descriptions are complete on the very first
ApiExplorer read after `MapWolverineEndpoints()` has run, even before the host starts. Only
Wolverine's descriptions are start-independent — ASP.NET's own minimal API descriptions still
compose at server start — so applications mixing the two should defer early ApiExplorer reads
until after startup.

## With Microsoft.Extensions.ApiDescription.Server

::: tip
If you are hitting the problems described below, consider the
[`openapi` command](#generating-the-openapi-document-at-the-command-line) instead — it was built
specifically to avoid the full application startup that `Microsoft.Extensions.ApiDescription.Server`
forces.
:::

Just a heads up, if you are trying to use [Microsoft.Extensions.ApiDescription.Server](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/openapi/aspnetcore-openapi?view=aspnetcore-9.0&tabs=net-cli%2Cvisual-studio-code#generate-openapi-documents-at-build-time) and
you get an `ObjectDisposedException` error on compilation against the `IServiceProvider`, follow these steps to fix:

1. Remove `Microsoft.Extensions.ApiDescription.Server` altogether
2. Just run `dotnet run` to see why your application isn't able to start correctly, and fix *that* problem
3. Add `Microsoft.Extensions.ApiDescription.Server` back

For whatever reason, the source generator for OpenAPI tries to start the entire application, including Wolverine's
`IHostedService`, and the whole thing blows up with that very unhelpful message if anything is wrong with the application.

Chances are good that one of the things preventing a successful startup is that Marten and Wolverine will, by default, begin performing their usual tasks immediately  upon startup. This entails connecting to the database, as well as to any external messaging providers you may be using. Since those connections are probably not going to be possible in your build environment, they will need to be disabled while the OpenApi generation is being done.

Microsoft's recomendation for detecting whether the application is running for the purpose of document generation is to use this code:
```cs
var generatingOpenApi = Assembly.GetEntryAssembly()?.GetName().Name == "GetDocument.Insider"
```

If this mode is detected, all connections can be disabled like so:
```cs
builder.Services.DisableAllExternalWolverineTransports();
builder.Services.DisableAllWolverineMessagePersistence();
```

Note that a syntactically valid connection string still needs to be provided to Marten, but it does not need to represent a real DB; a minimal placeholder is sufficient.
Also, if you are using the async daemon, you'll want to use the `DaemonMode.Disabled` mode.
```cs
if(generatingOpenApi)
{
    builder.Services
        .AddMarten(ConfigureMarten("Server=.;Database=Foo"))
        .AddAsyncDaemon(DaemonMode.Disabled)
        .UseLightweightSessions();
}
else
{
    // usual Marten config
}
```

## With NSwag

Be aware that if you want to use NSwag to generate a .NET/Typescript client for Wolverine.HTTP endpoints, you will need to add this line before `return await app.RunJasperFxCommands(args);`:

```cs
args = args.Where(arg => !arg.StartsWith("--applicationName")).ToArray();
```

See the full NSwag demo at https://github.com/JasperFx/wolverine/tree/main/src/Http/NSwagDemonstrator
