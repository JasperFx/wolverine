using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Headers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wolverine.Http.Runtime.MultiTenancy;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace Wolverine.Http;

public abstract class HttpHandler
{
    private readonly WolverineHttpOptions _options;
    private readonly JsonSerializerOptions _jsonOptions;

    // ReSharper disable once PublicConstructorInAbstractClass
    public HttpHandler(WolverineHttpOptions wolverineHttpOptions)
    {
        _options = wolverineHttpOptions;
        _jsonOptions = wolverineHttpOptions.JsonSerializerOptions.Value;
    }

    public async ValueTask<string?> TryDetectTenantId(HttpContext httpContext)
    {
        var tenantId = await _options.TryDetectTenantId(httpContext);
        if (tenantId != null)
        {
            Activity.Current?.SetTag(MetricsConstants.TenantIdKey, tenantId);
        }

        return tenantId;
    }

    public Task WriteTenantIdNotFound(HttpContext context)
    {
        return Results.Problem(new ProblemDetails
        {
            Status = 400,
            Detail = TenantIdDetection.NoMandatoryTenantIdCouldBeDetectedForThisHttpRequest
        }).ExecuteAsync(context);
    }

    public abstract Task Handle(HttpContext httpContext);

    public static string? ReadSingleHeaderValue(HttpContext context, string headerKey)
    {
        return context.Request.Headers[headerKey].SingleOrDefault();
    }

    public static string[] ReadManyHeaderValues(HttpContext context, string headerKey)
    {
        return context.Request.Headers[headerKey].ToArray()!;
    }

    public static IFormFile? ReadSingleFormFileValue(HttpContext context)
    {
        return context.Request.Form.Files.SingleOrDefault();
    }

    public static IFormFileCollection? ReadManyFormFileValues(HttpContext context)
    {
        return context.Request.Form.Files;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Task WriteString(HttpContext context, string text)
    {
        context.Response.ContentType = "text/plain";
        context.Response.ContentLength = text.Length;
        return context.Response.WriteAsync(text, context.RequestAborted);
    }

    public void ApplyHttpAware(object target, HttpContext context)
    {
        if (target is IHttpAware a) a.Apply(context);
    }

    private static bool isRequestJson(HttpContext context)
    {
        var contentType = context.Request.ContentType;
        if (contentType.IsEmpty())
        {
            return true; // Sure, we'll just go with this.
        }

        if (contentType.StartsWith("application/json") || contentType.StartsWith("text/json") || contentType.Contains("*/*"))
        {
            return true;
        }

        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public async ValueTask<(T?, HandlerContinuation)> ReadJsonAsync<T>(HttpContext context)
    {
        if (!isRequestJson(context))
        {
            context.Response.StatusCode = 415;
            return (default, HandlerContinuation.Stop);
        }

        if (!acceptsJson(context))
        {
            context.Response.StatusCode = 406;
            return (default, HandlerContinuation.Stop);
        }

        try
        {
            var body = await JsonSerializer.DeserializeAsync<T>(context.Request.Body, _jsonOptions,
                context.RequestAborted);

            return (body, HandlerContinuation.Continue);
        }
        catch (Exception e)
        {
            var logger = context.RequestServices.GetService<ILogger<T>>();
            logger?.LogError(e, "Error trying to deserialize JSON from incoming HTTP body at {Url} to type {Type}",
                context.Request.Path, typeof(T).FullNameInCode());

            if (e is JsonException jsonException)
            {
                await Results.Problem(new()
                {
                    Type = "https://httpstatuses.com/400",
                    Title = "Invalid JSON format",
                    Status = StatusCodes.Status400BadRequest,
                    Detail = jsonException.Message,
                    Instance = context.Request.Path,
                    Extensions =
                    {
                        { "lineNumber", jsonException.LineNumber ?? 0 },
                        { "bytePositionInLine", jsonException.BytePositionInLine ?? 0 }
                    }
                }).ExecuteAsync(context);
            }
            else
            {
                context.Response.StatusCode = 400;
            }

            return (default, HandlerContinuation.Stop);
        }
    }

    private static bool acceptsJson(HttpContext context)
    {
        var headers = new RequestHeaders(context.Request.Headers);

        if (!headers.Accept.Any())
        {
            return true;
        }

        return headers.Accept
            .Any(x => x.MediaType is
            {
                HasValue: true,
                Value: "application/json" or "application/problem+json" or "*/*" or "text/json"
            });
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Task WriteJsonAsync<T>(HttpContext context, T? body)
    {
        if (body == null)
        {
            context.Response.StatusCode = 404;
            return Task.CompletedTask;
        }

        return context.Response.WriteAsJsonAsync(body, _jsonOptions, context.RequestAborted);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Task WriteProblems(ProblemDetails details, HttpContext context)
    {
        return Results.Problem(details).ExecuteAsync(context);
    }
}