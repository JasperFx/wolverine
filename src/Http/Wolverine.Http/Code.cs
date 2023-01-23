using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using Microsoft.AspNetCore.Http;

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

    public Task WriteString(HttpContext context, string text)
    {
        context.Response.ContentType = "text/plain";
        context.Response.ContentLength = text.Length;
        return context.Response.WriteAsync(text, context.RequestAborted);
    }
}