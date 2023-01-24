using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace Wolverine.Http;

public abstract class EndpointHandler
{
    public abstract Task Handle(HttpContext httpContext);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Task WriteString(HttpContext context, string text)
    {
        context.Response.ContentType = "text/plain";
        context.Response.ContentLength = text.Length;
        return context.Response.WriteAsync(text, context.RequestAborted);
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask<T?> ReadJsonAsync<T>(HttpContext context, JsonSerializerOptions jsonOptions)
    {
        return JsonSerializer.DeserializeAsync<T>(context.Request.Body, jsonOptions, context.RequestAborted);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Task WriteJsonAsync<T>(HttpContext context, T body, JsonSerializerOptions jsonOptions)
    {
        return context.Response.WriteAsJsonAsync(body, jsonOptions, context.RequestAborted);
    }
}