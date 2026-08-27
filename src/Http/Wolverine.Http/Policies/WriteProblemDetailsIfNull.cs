using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using Microsoft.AspNetCore.Http;
using Wolverine.Util;

namespace Wolverine.Http.Policies;

internal class WriteProblemDetailsIfNull : AsyncFrame
{
    private Variable? _httpContext;

    public WriteProblemDetailsIfNull(Variable entity, Variable? identity, string message, int statusCode = 400)
    {
        Entity = entity;
        Identity = identity;
        Message = message;
        StatusCode = statusCode;
        
        uses.Add(Entity);
        if (Identity != null)
        {
            uses.Add(Identity);
        }
    }

    public Variable Entity { get; }

    /// <summary>
    /// Null when the entity was not addressed by a single identity value, which
    /// <c>AddStopConditionIfNull</c> allows for. <c>WriteProblems</c> already accepts a null
    /// identity.
    /// </summary>
    public Variable? Identity { get; }
    public string Message { get; }
    public int StatusCode { get; }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment("Write ProblemDetails if this required object is null");
        writer.Write($"BLOCK:if ({Entity.Usage} == null)");

        var identity = Identity?.Usage ?? "null";

        // The message can come straight from an [Entity(MissingMessage = "...")], so it is only ever
        // emitted as an escaped literal — see ToStringLiteral for why Constant.For will not do.
        var literal = Message.ToStringLiteral();

        if (Identity != null && Message.Contains("{0}"))
        {
            writer.Write($"await {nameof(HttpHandler.WriteProblems)}({StatusCode}, string.Format({literal}, {identity}), {_httpContext!.Usage}, {identity});");
        }
        else
        {
            writer.Write($"await {nameof(HttpHandler.WriteProblems)}({StatusCode}, {literal}, {_httpContext!.Usage}, {identity});");
        }

        writer.Write("return;");

        writer.FinishBlock();

        Next?.GenerateCode(method, writer);

    }
    
    
    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _httpContext = chain.FindVariable(typeof(HttpContext));
        yield return _httpContext;
    }
}
