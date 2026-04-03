using Microsoft.AspNetCore.Mvc;
using Wolverine.Http;

namespace WolverineWebApi;

// Custom exception types for testing
public class CustomHttpException : Exception
{
    public CustomHttpException(string message) : base(message) { }
}

public class SpecificHttpException : CustomHttpException
{
    public int StatusCode { get; }
    public SpecificHttpException(string message, int statusCode) : base(message)
    {
        StatusCode = statusCode;
    }
}

#region sample_on_exception_handler_level

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

#endregion

#region sample_on_exception_specific

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

#endregion

#region sample_on_exception_async

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

#endregion

#region sample_on_exception_with_finally

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

#endregion

#region sample_on_exception_no_match

/// <summary>
/// When the exception type doesn't match any OnException handler,
/// the exception should propagate normally
/// </summary>
public static class UnmatchedExceptionEndpoints
{
    [WolverineGet("/on-exception/unmatched")]
    public static string EndpointThatThrowsUnmatched()
    {
        throw new InvalidOperationException("No handler for this");
    }

    // Only handles CustomHttpException, not InvalidOperationException
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

#endregion

#region sample_on_exception_no_error

/// <summary>
/// When no exception is thrown, OnException should not be invoked
/// </summary>
public static class NoErrorEndpoints
{
    [WolverineGet("/on-exception/no-error")]
    public static string EndpointThatSucceeds()
    {
        return "All good";
    }

    public static ProblemDetails OnException(Exception ex)
    {
        return new ProblemDetails
        {
            Status = 500,
            Detail = "This should not be called"
        };
    }
}

#endregion

#region sample_on_exception_void_return

/// <summary>
/// OnException with void return — exception is still swallowed
/// </summary>
public static class VoidExceptionEndpoints
{
    public static string LastException = "";

    [WolverineGet("/on-exception/void")]
    public static string EndpointForVoidHandler()
    {
        throw new CustomHttpException("Void handler error");
    }

    public static void OnException(CustomHttpException ex)
    {
        LastException = ex.Message;
    }
}

#endregion
