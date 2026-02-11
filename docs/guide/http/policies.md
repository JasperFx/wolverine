## HTTP Policies

Custom policies can be created for HTTP endpoints through either creating your own implementation of `IHttpPolicy`
shown below:

<!-- snippet: sample_IHttpPolicy -->
<a id='snippet-sample_IHttpPolicy'></a>
```cs
/// <summary>
///     Use to apply your own conventions or policies to HTTP endpoint handlers
/// </summary>
public interface IHttpPolicy
{
    /// <summary>
    ///     Called during bootstrapping to alter how the message handlers are configured
    /// </summary>
    /// <param name="chains"></param>
    /// <param name="rules"></param>
    /// <param name="container">The application's underlying IoC Container</param>
    void Apply(IReadOnlyList<HttpChain> chains, GenerationRules rules, IServiceContainer container);
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/Wolverine.Http/IHttpPolicy.cs#L7-L23' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_IHttpPolicy' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

And then adding a policy to the `WolverineHttpOptions` like this code from the Fluent Validation extension for HTTP:

<!-- snippet: sample_usage_of_http_add_policy -->
<a id='snippet-sample_usage_of_http_add_policy'></a>
```cs
/// <summary>
///     Apply Fluent Validation middleware to all Wolverine HTTP endpoints with a known Fluent Validation
///     validator for the request type
/// </summary>
/// <param name="httpOptions"></param>
public static void UseFluentValidationProblemDetailMiddleware(this WolverineHttpOptions httpOptions)
{
    httpOptions.AddPolicy<HttpChainFluentValidationPolicy>();
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/Wolverine.Http.FluentValidation/WolverineHttpOptionsExtensions.cs#L7-L19' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_usage_of_http_add_policy' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Or lastly through lambdas (which creates an `IHttpPolicy` object behind the scenes):

<!-- snippet: sample_using_configure_endpoints -->
<a id='snippet-sample_using_configure_endpoints'></a>
```cs
app.MapWolverineEndpoints(opts =>
{
    // This is strictly to test the endpoint policy

    opts.ConfigureEndpoints(httpChain =>
    {
        // The HttpChain model is a configuration time
        // model of how the HTTP endpoint handles requests

        // This adds metadata for OpenAPI
        httpChain.WithMetadata(new CustomMetadata());
    });

    // more configuration for HTTP...

    // Opting into the Fluent Validation middleware from
    // Wolverine.Http.FluentValidation
    opts.UseFluentValidationProblemDetailMiddleware();

    // Or instead, you could use Data Annotations that are built
    // into the Wolverine.HTTP library
    opts.UseDataAnnotationsValidationProblemDetailMiddleware();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Program.cs#L215-L240' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_configure_endpoints' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The `HttpChain` model is a configuration time structure that Wolverine.Http will use at runtime to create the full
HTTP handler (RequestDelegate and RoutePattern for ASP.Net Core). But at bootstrapping / configuration time, we have
the option to add -- or remove -- any number of middleware, post processors, and custom metadata (OpenAPI or otherwise) 
for the endpoint.

Here's an example from the Wolverine.Http tests of using a policy to add custom metadata:

<!-- snippet: sample_using_configure_endpoints -->
<a id='snippet-sample_using_configure_endpoints'></a>
```cs
app.MapWolverineEndpoints(opts =>
{
    // This is strictly to test the endpoint policy

    opts.ConfigureEndpoints(httpChain =>
    {
        // The HttpChain model is a configuration time
        // model of how the HTTP endpoint handles requests

        // This adds metadata for OpenAPI
        httpChain.WithMetadata(new CustomMetadata());
    });

    // more configuration for HTTP...

    // Opting into the Fluent Validation middleware from
    // Wolverine.Http.FluentValidation
    opts.UseFluentValidationProblemDetailMiddleware();

    // Or instead, you could use Data Annotations that are built
    // into the Wolverine.HTTP library
    opts.UseDataAnnotationsValidationProblemDetailMiddleware();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Program.cs#L215-L240' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_configure_endpoints' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Resource Writer Policies

Wolverine has an additional type of policy that deals with how an endpoints primary result is handled. 

<!-- snippet: sample_IResourceWriterPolicy -->
<a id='snippet-sample_IResourceWriterPolicy'></a>
```cs
/// <summary>
///    Use to apply custom handling to the primary result of an HTTP endpoint handler
/// </summary>
public interface IResourceWriterPolicy
{
    /// <summary>
    ///  Called during bootstrapping to see whether this policy can handle the chain. If yes no further policies are tried.
    /// </summary>
    /// <param name="chain"> The chain to test against</param>
    /// <returns>True if it applies to the chain, false otherwise</returns>
    bool TryApply(HttpChain chain);
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/Wolverine.Http/Resources/IResourceWriterPolicy.cs#L3-L17' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_IResourceWriterPolicy' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Only one of these so called resource writer policies can apply to each endpoint and there are a couple of built in policies already.

If you need special handling of a primary return type you can implement `IResourceWriterPolicy` and register it in `WolverineHttpOptions`

<!-- snippet: sample_register_resource_writer_policy -->
<a id='snippet-sample_register_resource_writer_policy'></a>
```cs
opts.AddResourceWriterPolicy<CustomResourceWriterPolicy>();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Program.cs#L262-L266' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_register_resource_writer_policy' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Resource writer policies registered this way will be applied in order before all built in policies.
