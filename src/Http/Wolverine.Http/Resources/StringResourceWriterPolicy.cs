using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;

namespace Wolverine.Http.Resources;

internal class StringResourceWriterPolicy : IResourceWriterPolicy
{
    public bool TryApply(HttpChain chain)
    {
        if (chain.ResourceType == typeof(string))
        {
            chain.Postprocessors.Add(new WriteStringFrame(chain.Method.Creates.First()));

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

            writer.Write($"{prefix} {nameof(HttpHandler.WriteString)}(httpContext, {_result.Usage});");

            Next?.GenerateCode(method, writer);
        }

        public override void GenerateFSharpCode(GeneratedMethod method, ISourceWriter writer)
        {
            // HttpHandler.WriteString is static, so it resolves cleanly in F# (no `this`).
            var call =
                $"{typeof(HttpHandler).FSharpName()}.{nameof(HttpHandler.WriteString)}(httpContext, {_result.Usage})";

            // Inside a `task { }` body await it; otherwise it IS the trailing Task expression.
            writer.Write(method.AsyncMode == AsyncMode.AsyncTask ? $"do! {call}" : call);

            Next?.GenerateFSharpCode(method, writer);
        }
    }
}