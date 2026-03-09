using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Wolverine.Http.CodeGen;

/// <summary>
/// Frame that generates validation code for HTTP endpoints using RequirementResult.
/// Creates a ProblemDetails with status 400 and writes it to the response if Branch == Stop.
/// If Messages are empty, uses ProblemDetails.Detail = "Invalid Request".
/// </summary>
internal class RequirementResultHttpFrame : AsyncFrame
{
    private static int _count;
    private readonly Variable _variable;
    private Variable? _context;

    public RequirementResultHttpFrame(Variable variable)
    {
        _variable = variable;
        _variable.OverrideName(_variable.Usage + ++_count);
        uses.Add(_variable);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _context = chain.FindVariable(typeof(HttpContext));
        yield return _context;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment("Check RequirementResult and abort with ProblemDetails if Branch == Stop");
        writer.Write(
            $"BLOCK:if ({_variable.Usage}.{nameof(RequirementResult.Branch)} == {typeof(HandlerContinuation).FullNameInCode()}.{nameof(HandlerContinuation.Stop)})");
        writer.Write(
            $"var problemDetails{_count} = new {typeof(ProblemDetails).FullNameInCode()}();");
        writer.Write(
            $"problemDetails{_count}.{nameof(ProblemDetails.Status)} = 400;");
        writer.Write(
            $"problemDetails{_count}.{nameof(ProblemDetails.Title)} = \"Validation failed\";");
        writer.Write(
            $"BLOCK:if ({_variable.Usage}.{nameof(RequirementResult.Messages)}.Length > 0)");
        writer.Write(
            $"problemDetails{_count}.{nameof(ProblemDetails.Extensions)}[\"errors\"] = {_variable.Usage}.{nameof(RequirementResult.Messages)};");
        writer.FinishBlock();
        writer.Write("BLOCK:else");
        writer.Write(
            $"problemDetails{_count}.{nameof(ProblemDetails.Detail)} = \"Invalid Request\";");
        writer.FinishBlock();
        writer.Write(
            $"await {nameof(HttpHandler.WriteProblems)}(problemDetails{_count}, {_context!.Usage}).ConfigureAwait(false);");
        writer.Write("return;");
        writer.FinishBlock();
        writer.BlankLine();

        Next?.GenerateCode(method, writer);
    }
}
