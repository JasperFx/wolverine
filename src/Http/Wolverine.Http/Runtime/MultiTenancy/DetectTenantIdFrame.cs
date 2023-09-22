using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using Microsoft.AspNetCore.Http;
using Wolverine.Persistence;
using Wolverine.Runtime;

namespace Wolverine.Http.Runtime.MultiTenancy;

internal class DetectTenantIdFrame : AsyncFrame
{
    private readonly TenantIdDetection _options;
    private readonly HttpChain _chain;
    private Variable _httpContext;
    private Variable _messageContext;

    public DetectTenantIdFrame(TenantIdDetection options, HttpChain chain)
    {
        _options = options;
        _chain = chain;

        TenantId = new Variable( typeof(string), PersistenceConstants.TenantIdVariableName, this);
    }

    public Variable TenantId { get; }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _httpContext = chain.FindVariable(typeof(HttpContext));
        yield return _httpContext;

        _messageContext = chain.FindVariable(typeof(MessageContext));
        yield return _messageContext;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.BlankLine();
        writer.WriteComment("Tenant Id detection");
        for (int i = 0; i < _options.Strategies.Count ; i++)
        {
            writer.WriteComment($"{i + 1}. {_options.Strategies[i]}");
        }
        
        writer.Write($"var {TenantId.Usage} = await {nameof(HttpHandler.TryDetectTenantId)}({_httpContext.Usage});");
        writer.Write($"{_messageContext.Usage}.{nameof(MessageContext.TenantId)} = {TenantId.Usage};");

        if (_options.ShouldAssertTenantIdExists(_chain))
        {
            writer.Write($"BLOCK:if (string.{nameof(string.IsNullOrEmpty)}({TenantId.Usage}))");
            writer.Write($"await {nameof(HttpHandler.WriteTenantIdNotFound)}({_httpContext.Usage});");
            writer.Write("return;");
            writer.FinishBlock();
        }
        
        Next?.GenerateCode(method, writer);
    }
}