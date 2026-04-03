# Exception Handling

Wolverine supports an `OnException` / `OnExceptionAsync` naming convention for middleware methods that allows you to handle exceptions thrown during endpoint execution. This is the recommended approach for structured exception handling in Wolverine HTTP endpoints.

## Handler-Level Exception Handling

The simplest approach is to add `OnException` methods directly on your endpoint class. The first parameter must be the exception type to catch:

<!-- snippet: sample_on_exception_handler_level -->
<a id='snippet-sample_on_exception_handler_level'></a>
```cs
/// <summary>
/// Handler-level OnException: the exception handler is a method on the same class
/// as the endpoint handler itself
/// </summary>
public static class OnExceptionEndpoints
{
    [WolverineGet("/on-exception/simple")]
    public static string SimpleEndpointThatThrows()
    {
        throw new CustomHttpException("Something went wrong");
    }

    public static ProblemDetails OnException(CustomHttpException ex)
    {
        return new ProblemDetails
        {
            Status = 500,
            Detail = ex.Message,
            Title = "Custom Error"
        };
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/OnExceptionEndpoints.cs#L23-L49' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_on_exception_handler_level' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Key behaviors:
- The exception is **swallowed** after `OnException` handles it — no re-throw
- Returning `ProblemDetails` writes a proper `application/problem+json` response
- If no `OnException` method matches the thrown exception type, the exception propagates normally

## Multiple Exception Types

You can define multiple `OnException` methods for different exception types. Wolverine automatically orders catch blocks by specificity — the most derived exception types are matched first:

<!-- snippet: sample_on_exception_specific -->
<a id='snippet-sample_on_exception_specific'></a>
```cs
/// <summary>
/// Handler with multiple exception handlers, testing specificity ordering
/// </summary>
public static class MultipleExceptionEndpoints
{
    [WolverineGet("/on-exception/specific")]
    public static string EndpointThatThrowsSpecific()
    {
        throw new SpecificHttpException("Specific error", 422);
    }

    [WolverineGet("/on-exception/general")]
    public static string EndpointThatThrowsGeneral()
    {
        throw new CustomHttpException("General error");
    }

    // More specific — should be matched first for SpecificHttpException
    public static ProblemDetails OnException(SpecificHttpException ex)
    {
        return new ProblemDetails
        {
            Status = ex.StatusCode,
            Detail = ex.Message,
            Title = "Specific Error"
        };
    }

    // Less specific — catches CustomHttpException (but not SpecificHttpException)
    public static ProblemDetails OnException(CustomHttpException ex)
    {
        return new ProblemDetails
        {
            Status = 500,
            Detail = ex.Message,
            Title = "General Error"
        };
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/OnExceptionEndpoints.cs#L51-L94' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_on_exception_specific' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Async Exception Handlers

Use `OnExceptionAsync` for async exception handling:

<!-- snippet: sample_on_exception_async -->
<a id='snippet-sample_on_exception_async'></a>
```cs
/// <summary>
/// Async OnException handler
/// </summary>
public static class AsyncExceptionEndpoints
{
    [WolverineGet("/on-exception/async")]
    public static string EndpointThatThrowsForAsync()
    {
        throw new CustomHttpException("Async error");
    }

    public static Task<ProblemDetails> OnExceptionAsync(CustomHttpException ex)
    {
        var problem = new ProblemDetails
        {
            Status = 500,
            Detail = ex.Message,
            Title = "Async Error"
        };
        return Task.FromResult(problem);
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/OnExceptionEndpoints.cs#L96-L121' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_on_exception_async' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Combining with Finally

`OnException` works with `Finally` methods. When an exception is thrown and caught by `OnException`, the `Finally` block still runs:

<!-- snippet: sample_on_exception_with_finally -->
<a id='snippet-sample_on_exception_with_finally'></a>
```cs
/// <summary>
/// OnException combined with Finally, testing interaction
/// </summary>
public static class ExceptionWithFinallyEndpoints
{
    public static readonly List<string> Actions = new();

    [WolverineGet("/on-exception/with-finally")]
    public static string EndpointWithFinally()
    {
        Actions.Add("Handler");
        throw new CustomHttpException("Error with finally");
    }

    public static ProblemDetails OnException(CustomHttpException ex)
    {
        Actions.Add("OnException");
        return new ProblemDetails
        {
            Status = 500,
            Detail = ex.Message,
            Title = "Error"
        };
    }

    public static void Finally()
    {
        Actions.Add("Finally");
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/OnExceptionEndpoints.cs#L123-L157' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_on_exception_with_finally' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The execution order is: Handler (throws) -> OnException -> Finally

## Exception Handling as Middleware

You can also apply `OnException` handlers as middleware across multiple endpoints:

<!-- snippet: sample_on_exception_middleware -->
<a id='snippet-sample_on_exception_middleware'></a>
```cs
/// <summary>
/// A middleware class that provides exception handling via the OnException convention.
/// Applied globally via AddMiddleware in Program.cs
/// </summary>
public static class GlobalExceptionMiddleware
{
    public static ProblemDetails OnException(CustomHttpException ex)
    {
        return new ProblemDetails
        {
            Status = 500,
            Detail = ex.Message,
            Title = "Global Error Handler"
        };
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/OnExceptionMiddleware.cs#L6-L24' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_on_exception_middleware' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Register it in your application setup:

```csharp
app.MapWolverineEndpoints(opts =>
{
    // Apply to all endpoints
    opts.AddMiddleware(typeof(GlobalExceptionMiddleware));

    // Or apply to specific endpoints
    opts.AddMiddleware(typeof(GlobalExceptionMiddleware),
        chain => chain.Method.HandlerType.Namespace == "MyApp.Api");
});
```

## Return Value Semantics

`OnException` methods support the same return value semantics as `Before` middleware methods:

| Return Type | Behavior |
|------------|----------|
| `void` / `Task` | Exception is swallowed, no response body written |
| `ProblemDetails` | Writes `application/problem+json` response |
| `IResult` | Executes the `IResult` (e.g., `Results.StatusCode(503)`) |
| `HandlerContinuation` | Controls whether processing continues |
| `OutgoingMessages` | Publishes cascading messages |

## How It Works

At code generation time, Wolverine wraps your endpoint handler in a `try/catch/finally` block:

```csharp
// Generated code (simplified)
try
{
    // Before middleware
    // Handler execution
    // After middleware / resource writing
}
catch (SpecificHttpException specificHttpException)
{
    var problemDetails = OnException(specificHttpException);
    await WriteProblems(problemDetails, httpContext);
    return;
}
catch (CustomHttpException customHttpException)
{
    var problemDetails = OnException(customHttpException);
    await WriteProblems(problemDetails, httpContext);
    return;
}
finally
{
    Finally();
}
```

Catch blocks are ordered by inheritance depth, with the most specific exception types first. This is computed at build time — there is no runtime reflection or if/else branching.

## Using the Attribute

You can also mark methods with the `[WolverineOnException]` attribute instead of relying on the naming convention:

```csharp
public static class MyMiddleware
{
    [WolverineOnException]
    public static ProblemDetails HandleError(CustomHttpException ex)
    {
        return new ProblemDetails
        {
            Status = 500,
            Detail = ex.Message
        };
    }
}
```

## Interaction with Error Handling Policies

The `OnException` convention is separate from Wolverine's policy-based error handling (`opts.OnException().Retry()`, etc.). The convention-based `OnException` handlers run first — if they catch the exception, it is swallowed and policy-based retries never fire. If no `OnException` handler matches the thrown exception type, the exception propagates normally and policy-based error handling kicks in.
