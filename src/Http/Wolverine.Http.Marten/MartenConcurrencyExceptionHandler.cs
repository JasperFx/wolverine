using JasperFx;
using Marten.Exceptions;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Wolverine.Http.Marten;

/// <summary>
///     Exception handler for the ASP.NET Core exception handler middleware that maps Marten /
///     JasperFx concurrency failures to a ProblemDetails response with a configurable status code
///     (409 Conflict by default) instead of a 500. Register through
///     <see cref="WolverineHttpOptionsExtensions.UseProblemDetailsForConcurrencyExceptions" />,
///     and make sure the application calls <c>app.UseExceptionHandler()</c> so this handler
///     actually runs
/// </summary>
public class MartenConcurrencyExceptionHandler : IExceptionHandler
{
    private readonly int _statusCode;
    private readonly ILogger<MartenConcurrencyExceptionHandler> _logger;

    public MartenConcurrencyExceptionHandler(int statusCode, ILogger<MartenConcurrencyExceptionHandler> logger)
    {
        _statusCode = statusCode;
        _logger = logger;
    }

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception,
        CancellationToken cancellationToken)
    {
        // StreamLockedException does not inherit from ConcurrencyException, but can only ever be
        // thrown by the exclusive locking load path (FetchForExclusiveWriting), so mapping it
        // unconditionally is safe
        if (exception is not ConcurrencyException and not StreamLockedException)
        {
            return false;
        }

        // An escaping exception used to leave an error log behind, so keep a signal
        // for operators now that the exception stops here
        _logger.LogInformation("Handled {ExceptionType} on {Method} {Path} as HTTP {StatusCode}",
            exception.GetType().Name, httpContext.Request.Method, httpContext.Request.Path, _statusCode);

        httpContext.Response.StatusCode = _statusCode;

        var problemDetailsService = httpContext.RequestServices.GetRequiredService<IProblemDetailsService>();
        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = new ProblemDetails
            {
                Title = "Concurrency conflict",
                Detail = exception.Message,
                Status = _statusCode
            }
        });
    }
}
