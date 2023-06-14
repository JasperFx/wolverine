using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using Microsoft.AspNetCore.Http;

namespace Wolverine.Http.Resources;

internal class StatusCodePolicy : IResourceWriterPolicy
{
    public bool TryApply(HttpChain chain)
    {
        if (chain.ResourceType == typeof(int))
        {
            var writeStatusCode = new WriteStatusCodeFrame(chain.Method.Creates.First());
            chain.Postprocessors.Add(writeStatusCode);
            return true;
        }

        return false;
    }
}

internal class WriteStatusCodeFrame : SyncFrame
{
    private readonly Variable _statusCode;
    private Variable? _context;

    public WriteStatusCodeFrame(Variable statusCode)
    {
        _statusCode = statusCode;
        uses.Add(statusCode);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _context = chain.FindVariable(typeof(HttpContext));
        yield return _context;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.Write(
            $"{_context!.Usage}.{nameof(HttpContext.Response)}.{nameof(HttpResponse.StatusCode)} = {_statusCode.Usage};");
    }
}