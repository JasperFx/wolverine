# Fluent Validation Middleware for HTTP

Wolverine.Http has a separate package called `WolverineFx.Http.FluentValidation` that provides a simple middleware
for using [Fluent Validation](https://docs.fluentvalidation.net/en/latest/) in your HTTP endpoints.

To get started, install that Nuget reference:

```bash
dotnet add package WolverineFx.Http.FluentValidation
```

Next, let's assume that you have some Fluent Validation validators registered in your application container for the
request types of your HTTP endpoints -- and the [UseFluentValidation](/guide/handlers/fluent-validation) method from the 
`WolverineFx.FluentValidation` package will help find these validators and register them in a way that optimizes this
middleware usage.

Next, add this one single line of code to your Wolverine.Http bootstrapping:

```csharp
opts.UseFluentValidationProblemDetailMiddleware();
```

as shown in context below in an application shown below:

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
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Program.cs#L165-L186' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_configure_endpoints' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->
