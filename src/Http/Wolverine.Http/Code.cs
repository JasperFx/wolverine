using System.Runtime.CompilerServices;
using System.Text.Json;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;

namespace Wolverine.Http;

public interface IResourceWriterPolicy
{
    bool TryApply(EndpointChain chain);
}

internal class StringResourceWriterPolicy : IResourceWriterPolicy
{
    public bool TryApply(EndpointChain chain)
    {
        if (chain.ResourceType == typeof(string))
        {
            chain.Postprocessors.Add(new WriteStringFrame(chain.Method.ReturnVariable));

            return true;
        }

        return false;
    }

    internal class WriteStringFrame : AsyncFrame
    {
        private readonly Variable _result;

        public WriteStringFrame(Variable result)
        {
            _result = result;
            uses.Add(_result);
        }

        public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
        {
            var prefix = method.AsyncMode == AsyncMode.ReturnCompletedTask ? "return" : "await";  
            
            writer.Write($"{prefix} {nameof(EndpointHandler.WriteString)}(httpContext, {_result.Usage});");
            
            Next?.GenerateCode(method, writer);
        }
    }
}



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

