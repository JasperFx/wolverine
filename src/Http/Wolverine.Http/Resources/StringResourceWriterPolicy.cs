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
        // GH-4789: ResourceVariable first, Method.Creates.First() only as the fallback. These policies
        // gate on chain.ResourceType, and IChain.UseForResponse CHANGES that -- it is how an IResponseAware
        // such as Marten's UpdatedAggregate swaps the declared response for another call's return value. So
        // reading the handler method's own first created variable instead wrote the WRONG one whenever a
        // response had been rewritten, generating code that did not compile. JsonResourceWriterPolicy and
        // ContentNegotiationPolicy already did this correctly, which is why the JSON path (and therefore
        // UpdatedAggregate) worked while the string, IResult and status-code paths did not.
            var resourceVariable = chain.ResourceVariable ?? chain.Method.Creates.First();
            chain.Postprocessors.Add(new WriteStringFrame(resourceVariable,
                chain.MissingResponseBodyStatusCode));

            return true;
        }

        return false;
    }

    internal class WriteStringFrame : AsyncFrame
    {
        private readonly Variable _result;
        private readonly int _missingStatusCode;

        public WriteStringFrame(Variable result, int missingStatusCode = 404)
        {
            _result = result;
            _missingStatusCode = missingStatusCode;
            uses.Add(_result);
        }

        public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
        {
            var prefix = method.AsyncMode == AsyncMode.ReturnCompletedTask ? "return" : "await";

            writer.Write(
                $"{prefix} {nameof(HttpHandler.WriteString)}(httpContext, {_result.Usage}, {_missingStatusCode});");

            Next?.GenerateCode(method, writer);
        }

        public override void GenerateFSharpCode(GeneratedMethod method, ISourceWriter writer)
        {
            // HttpHandler.WriteString is static, so it resolves cleanly in F# (no `this`).
            var call =
                $"{typeof(HttpHandler).FSharpName()}.{nameof(HttpHandler.WriteString)}(httpContext, {_result.Usage}, {_missingStatusCode})";

            // Inside a `task { }` body await it; otherwise it IS the trailing Task expression.
            writer.Write(method.AsyncMode == AsyncMode.AsyncTask ? $"do! {call}" : call);

            Next?.GenerateFSharpCode(method, writer);
        }
    }
}