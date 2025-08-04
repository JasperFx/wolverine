using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using Microsoft.AspNetCore.Http;

namespace Wolverine.Http.Policies;

internal class WriteProblemDetailsIfNull : AsyncFrame
{
    private Variable _httpContext;

    public WriteProblemDetailsIfNull(Variable entity, Variable identity, string message, int statusCode = 400)
    {
        Entity = entity;
        Identity = identity;
        Message = message;
        StatusCode = statusCode;
        
        uses.Add(Entity);
        uses.Add(Identity);
    }

    public Variable Entity { get; }
    public Variable Identity { get; }
    public string Message { get; }
    public int StatusCode { get; }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment("Write ProblemDetails if this required object is null");
        writer.Write($"BLOCK:if ({Entity.Usage} == null)");

        if (Message.Contains("{0}"))
        {
            writer.Write($"await {nameof(HttpHandler.WriteProblems)}({StatusCode}, string.Format(\"{Message}\", {Identity.Usage}), {_httpContext.Usage}, {Identity.Usage});");
        }
        else
        {
            var constant = Constant.For(Message);
            writer.Write($"await {nameof(HttpHandler.WriteProblems)}({StatusCode}, {constant.Usage}, {_httpContext.Usage}, {Identity.Usage});");
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