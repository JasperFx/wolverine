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
        // GH-4789: ResourceVariable first, Method.Creates.First() only as the fallback. These policies
        // gate on chain.ResourceType, and IChain.UseForResponse CHANGES that -- it is how an IResponseAware
        // such as Marten's UpdatedAggregate swaps the declared response for another call's return value. So
        // reading the handler method's own first created variable instead wrote the WRONG one whenever a
        // response had been rewritten, generating code that did not compile. JsonResourceWriterPolicy and
        // ContentNegotiationPolicy already did this correctly, which is why the JSON path (and therefore
        // UpdatedAggregate) worked while the string, IResult and status-code paths did not.
            var writeStatusCode = new WriteStatusCodeFrame(chain.ResourceVariable ?? chain.Method.Creates.First());
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