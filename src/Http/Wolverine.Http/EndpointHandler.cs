using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace Wolverine.Http;

public abstract class EndpointHandler
{
    private readonly WolverineHttpOptions _options;

    // ReSharper disable once PublicConstructorInAbstractClass
    public EndpointHandler(WolverineHttpOptions options)
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
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask<T?> ReadJsonAsync<T>(HttpContext context)
    {
        return JsonSerializer.DeserializeAsync<T>(context.Request.Body, _options.JsonSerializerOptions, context.RequestAborted);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Task WriteJsonAsync<T>(HttpContext context, T body)
    {
        return context.Response.WriteAsJsonAsync(body, _options.JsonSerializerOptions, context.RequestAborted);
    }
}