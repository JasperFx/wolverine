# HTTP Middleware

Creating and applying middleware to HTTP endpoints is very similar to [middleware for message handlers](/guide/handlers/middleware) with
just a couple differences:

1. As shown in a later section, you can use `IResult` from ASP.Net Core Middleware for conditionally stopping request handling *in addition to* the
   `HandlerContinuation` approach from message handlers
2. Use the `IHttpPolicy` interface instead of `IHandlerPolicy` to conventionally apply middleware to only HTTP endpoints
3. Your middleware types can take in `HttpContext` and any other related services that Wolverine supports for HTTP endpoints in addition to IoC services

The `[Middleware]` attribute from message handlers works on HTTP endpoint methods. 


## Conditional Endpoint Continuations

For message handlers, you use the [`HandlerContinuation`](/guide/handlers/middleware.html#conditionally-stopping-the-message-handling) 
to conditionally stop message handling in middleware that executes before the main handler. Likewise, you can do the
same thing in HTTP endpoints, but instead use the [IResult](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/minimal-apis/responses?view=aspnetcore-7.0)
concept from ASP.Net Core Minimal API.

As an example, let's say that you are using some middleware to do custom authentication filtering that stops processing
with a 401 status code. Here's a bit of middleware from the Wolverine tests that does just that:

<!-- snippet: sample_fake_authentication_middleware -->
<a id='snippet-sample_fake_authentication_middleware'></a>
```cs
public class FakeAuthenticationMiddleware
{
    public static IResult Before(IAmAuthenticated message)
    {
        return message.Authenticated 
            // This tells Wolverine to just keep going
            ? WolverineContinue.Result() 
            
            // If the IResult is not WolverineContinue, Wolverine
            // will execute the IResult and stop processing otherwise
            : Results.Unauthorized();
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/MiddlewareEndpoints.cs#L103-L119' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_fake_authentication_middleware' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The key point to notice there is that `IResult` is a "return value" of the middleware. In the case of an HTTP endpoint,
Wolverine will check if that `IResult` is a `WolverineContinue` object, and if so, will continue processing. If the `IResult`
object is anything else, Wolverine will execute that `IResult` and stop processing the HTTP request otherwise. 

For a little more complex example, here's part of the Fluent Validation middleware for Wolverine.Http:

<!-- snippet: sample_FluentValidationHttpExecutor_ExecuteOne -->
<a id='snippet-sample_fluentvalidationhttpexecutor_executeone'></a>
```cs
[MethodImpl(MethodImplOptions.AggressiveInlining)]
public static async Task<IResult> ExecuteOne<T>(IValidator<T> validator, IProblemDetailSource<T> source, T message)
{
    // First, validate the incoming request of type T
    var result = await validator.ValidateAsync(message);
        
    // If there are any errors, create a ProblemDetails result and return
    // that to write out the validation errors and otherwise stop processing
    if (result.Errors.Any())
    {
        var details = source.Create(message, result.Errors);
        return Results.Problem(details);
    }

    // Everything is good, full steam ahead!
    return WolverineContinue.Result();
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/Wolverine.Http.FluentValidation/Internals/FluentValidationHttpExecutor.cs#L9-L29' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_fluentvalidationhttpexecutor_executeone' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Using Configure(chain) Methods

You can make explicit modifications to HTTP processing for middleware or OpenAPI metadata for a single endpoint (really all
endpoint methods on that type) using the `public static void Configure(HttpChain)` convention.

Let's say you have a bit of custom middleware for HTTP endpoints like so:

<!-- snippet: sample_http_stopwatch_middleware -->
<a id='snippet-sample_http_stopwatch_middleware'></a>
```cs
public class StopwatchMiddleware
{
    private readonly Stopwatch _stopwatch = new();

    public void Before()
    {
        _stopwatch.Start();
    }

    public void Finally(ILogger logger, HttpContext context)
    {
        _stopwatch.Stop();
        logger.LogDebug("Request for route {Route} ran in {Duration} milliseconds",
            context.Request.Path, _stopwatch.ElapsedMilliseconds);
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/MiddlewareEndpoints.cs#L10-L29' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_http_stopwatch_middleware' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

And you want to apply it to a single HTTP endpoint without having to dirty your hands with an attribute. You can use that naming
convention up above like so:

sample: sample_applying_middleware_programmatically_to_one_chain


## Apply Middleware by Policy

To apply middleware to selected HTTP endpoints by some kind of policy, you can use the `IHttpPolicy` type
to analyze and apply middleware to some subset of HTTP endpoints. As an example from Wolverine.Http itself,
this middleware is applied to any endpoint that also uses Wolverine message publishing to apply tracing information
from the `HttpContext` to subsequent Wolverine messages published during the request:

<!-- snippet: sample_RequestIdMiddleware -->
<a id='snippet-sample_requestidmiddleware'></a>
```cs
public static class RequestIdMiddleware
{
    public const string CorrelationIdHeaderKey = "X-Correlation-ID";
    
    // Remember that most Wolverine middleware can be done with "just" a method
    public static void Apply(HttpContext httpContext, IMessageContext messaging)
    {
        if (httpContext.Request.Headers.TryGetValue(CorrelationIdHeaderKey, out var correlationId))
        {
            messaging.CorrelationId = correlationId.First();
        }
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/Wolverine.Http/Runtime/RequestIdMiddleware.cs#L10-L26' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_requestidmiddleware' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

And a matching `IHttpPolicy` to apply that middleware to any HTTP endpoint where there is a dependency on Wolverine's `IMessageContext` or `IMessageBus`:

<!-- snippet: sample_RequestIdPolicy -->
<a id='snippet-sample_requestidpolicy'></a>
```cs
internal class RequestIdPolicy : IHttpPolicy
{
    public void Apply(IReadOnlyList<HttpChain> chains, GenerationRules rules, IContainer container)
    {
        foreach (var chain in chains)
        {
            var serviceDependencies = chain.ServiceDependencies(container, Type.EmptyTypes).ToArray();
            if (serviceDependencies.Contains(typeof(IMessageContext)) ||
                serviceDependencies.Contains(typeof(IMessageBus)))
            {
                chain.Middleware.Insert(0, new MethodCall(typeof(RequestIdMiddleware), nameof(RequestIdMiddleware.Apply)));
            }
        }
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/Wolverine.Http/Runtime/RequestIdMiddleware.cs#L28-L46' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_requestidpolicy' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Lastly, this particular policy is included by default, but if it wasn't, this is the code to apply it explicitly:

<!-- snippet: sample_adding_http_policy -->
<a id='snippet-sample_adding_http_policy'></a>
```cs
// app is a WebApplication
app.MapWolverineEndpoints(opts =>
{
    // add the policy to Wolverine HTTP endpoints
    opts.AddPolicy<RequestIdPolicy>();
});
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/Wolverine.Http/Runtime/RequestIdMiddleware.cs#L55-L64' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_adding_http_policy' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

For simpler middleware application, you could also use this feature:

<!-- snippet: sample_simple_middleware_policy_for_http -->
<a id='snippet-sample_simple_middleware_policy_for_http'></a>
```cs
app.MapWolverineEndpoints(opts =>
{
    // Fake policy to add authentication middleware to any endpoint classes under
    // an application namespace
    opts.Middleware.AddType(typeof(MyAuthenticationMiddleware),
        c => c.HandlerCalls().Any(x => x.HandlerType.IsInNamespace("MyApp.Authenticated")));
});
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/Wolverine.Http/Runtime/RequestIdMiddleware.cs#L66-L76' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_simple_middleware_policy_for_http' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->





