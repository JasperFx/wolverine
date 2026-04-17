using Microsoft.AspNetCore.Mvc;
using Wolverine.Http;

namespace WolverineWebApi;

#region sample_on_exception_middleware
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

#endregion

/// <summary>
/// Endpoint class that does NOT have its own OnException method.
/// Relies on the globally applied middleware.
/// </summary>
public static class MiddlewareExceptionEndpoints
{
    [WolverineGet("/on-exception/middleware")]
    public static string EndpointReliesOnMiddleware()
    {
        throw new CustomHttpException("Handled by middleware");
    }
}
