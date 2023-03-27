using System.Runtime.CompilerServices;
using System.Text.Json;
using JasperFx.CodeGeneration;
using JasperFx.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Wolverine.Http;

public abstract class HttpHandler
{
    private readonly WolverineHttpOptions _options;

    // ReSharper disable once PublicConstructorInAbstractClass
    public HttpHandler(WolverineHttpOptions options)
    {
        _options = options;
    }

    public abstract Task Handle(HttpContext httpContext);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Task WriteString(HttpContext context, string text)
    {
        context.Response.ContentType = "text/plain";
        context.Response.ContentLength = text.Length;
        return context.Response.WriteAsync(text, context.RequestAborted);
    }

    private static bool isRequestJson(HttpContext context)
    {
        var contentType = context.Request.ContentType;
        if (contentType.IsEmpty())
        {
            return true; // Sure, we'll just go with this.
        }

        if (contentType.EqualsIgnoreCase("application/json") || contentType.EqualsIgnoreCase("text/json"))
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
            var body = await JsonSerializer.DeserializeAsync<T>(context.Request.Body, _options.JsonSerializerOptions,
                context.RequestAborted);

            return (body, HandlerContinuation.Continue);
        }
        catch (Exception e)
        {
            var logger = context.RequestServices.GetService<ILogger<T>>();
            logger?.LogError(e, "Error trying to deserialize JSON from incoming HTTP body at {Url} to type {Type}",
                context.Request.Path, typeof(T).FullNameInCode());

            context.Response.StatusCode = 400;
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

        if (headers.Accept.Any(x => x.MediaType.HasValue && x.MediaType.Value == "application/json"))
        {
            return true;
        }

        if (headers.Accept.Any(x => x.MediaType.HasValue && x.MediaType.Value == "text/json"))
        {
            return true;
        }

        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Task WriteJsonAsync<T>(HttpContext context, T? body)
    {
        return context.Response.WriteAsJsonAsync(body, _options.JsonSerializerOptions, context.RequestAborted);
    }
}