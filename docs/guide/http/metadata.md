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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/SignupEndpoint.cs#L6-L21' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_openapi_attributes' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/Wolverine.Http/Runtime/PublishingEndpoint.cs#L15-L34' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_programmatic_one_off_openapi_metadata' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Swashbuckle and Wolverine

[Swashbuckle](https://github.com/domaindrivendev/Swashbuckle.AspNetCore) is de facto the default OpenAPI tooling and it is added in by the default `dotnet new` templates for ASP.Net Core
applications. It's also very MVC Core-centric in its assumptions about how to generate OpenAPI metadata to describe endpoints.
If you need to (or just want to), you can do quite a bit to control exactly how Swashbuckle works against
Wolverine endpoints by using a custom `IOperationFilter` of your making that can use Wolverine's own `HttpChain` model
for finer grained control. Here's a sample from the Wolverine testing code that just uses Wolverine' own model to
determine the OpenAPI operation id:

<!-- snippet: sample_WolverineOperationFilter -->
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/WolverineOperationFilter.cs#L7-L23' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_wolverineoperationfilter' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

And that would be registered with Swashbuckle inside of your `Program.Main()` method like so:

<!-- snippet: sample_register_custom_swashbuckle_filter -->
<a id='snippet-sample_register_custom_swashbuckle_filter'></a>
```cs
builder.Services.AddSwaggerGen(x =>
{
    x.OperationFilter<WolverineOperationFilter>();
});
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Program.cs#L43-L50' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_register_custom_swashbuckle_filter' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/FakeEndpoint.cs#L13-L23' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_override_operation_id_for_openapi' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## IHttpAware or IEndpointMetadataProvider Models

Wolverine honors the ASP.Net Core [IEndpointMetadataProvider](https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.http.metadata.iendpointmetadataprovider?view=aspnetcore-7.0)
interface on resource types to add or modify endpoint metadata.

If you want Wolverine to automatically apply metadata (and HTTP runtime behavior) based on the resource type of 
an HTTP endpoint, you can have your response type implement the `IHttpAware` interface from Wolverine. As an example, 
consider the `CreationResponse` type in Wolverine:

<!-- snippet: sample_CreationResponse -->
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/Wolverine.Http/IHttpAware.cs#L81-L107' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_creationresponse' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Any endpoint that returns `CreationResponse` or a sub class will automatically expose a status code of `201` for successful
processing to denote resource creation instead of the generic `200`. Same goes for the built-in `AcceptResponse` type, but returning `202` status. Your own custom implementations of the `IHttpAware`
interface would apply the metadata declarations at configuration time so that those customizations would be part of the
exported Swashbuckle documentation of the system.

As of Wolverine 3.4, Wolverine will also apply OpenAPI metadata from any value created by compound handler middleware
or other middleware that implements the `IEndpointMetadataProvider` interface -- which many `IResult` implementations
from within ASP.Net Core middleware do. Consider this example from the tests:

snippet: sample_using_optional_iresult_with_openapi_metadata
