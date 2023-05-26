using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using Microsoft.AspNetCore.Http;

namespace Wolverine.Http.Resources;

internal class EmptyBody204Policy : IResourceWriterPolicy
{
    public bool TryApply(HttpChain chain)
    {
        if (chain.ResourceType == null || chain.ResourceType == typeof(void))
        {
            chain.Postprocessors.Insert(0, new WriteEmptyBodyStatusCode());
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
        writer.Write($"{_context!.Usage}.{nameof(HttpContext.Response)}.{nameof(HttpResponse.StatusCode)} = 204;");
        Next?.GenerateCode(method, writer);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _context = chain.FindVariable(typeof(HttpContext));
        yield return _context;
    }
}