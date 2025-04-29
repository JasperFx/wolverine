using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using Microsoft.AspNetCore.Http;
using Microsoft.FSharp.Core;

namespace Wolverine.Http.Resources;

internal class EmptyBody204Policy : IResourceWriterPolicy
{
    public bool TryApply(HttpChain chain)
    {
        if (chain.ResourceType == null || chain.ResourceType == typeof(void) || chain.ResourceType == typeof(Unit))
        {
            chain.Postprocessors.Insert(0, new WriteEmptyBodyStatusCode());
            chain.Metadata.Produces(204);
            return true;
        }

        return false;
    }
}

internal class WriteEmptyBodyStatusCode : SyncFrame
{
    private Variable? _context;

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment("Wolverine automatically sets the status code to 204 for empty responses");
        // Only change the status code if it wasn't already set by the user's handler (default is 200).
        writer.Write($"if ({_context!.Usage}.{nameof(HttpContext.Response)} is {{ {nameof(HttpResponse.HasStarted)}: false, {nameof(HttpResponse.StatusCode)}: 200 }}) {_context!.Usage}.{nameof(HttpContext.Response)}.{nameof(HttpResponse.StatusCode)} = 204;");
        Next?.GenerateCode(method, writer);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _context = chain.FindVariable(typeof(HttpContext));
        yield return _context;
    }
}