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
    private readonly int _index;
    private readonly Variable _variable;
    private Variable? _context;

    // GH-4714: numbered per CHAIN rather than from a process-wide static, so this chain's generated code
    // depends only on this chain. Index 0 keeps the bare name.
    public RequirementResultHttpFrame(Variable variable, int index)
    {
        _variable = variable;
        _index = index;

        if (index > 0)
        {
            _variable.OverrideName(_variable.Usage + index);
        }

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
            $"var problemDetails{_index} = new {typeof(ProblemDetails).FullNameInCode()}();");
        writer.Write(
            $"problemDetails{_index}.{nameof(ProblemDetails.Status)} = 400;");
        writer.Write(
            $"problemDetails{_index}.{nameof(ProblemDetails.Title)} = \"Validation failed\";");
        writer.Write(
            $"BLOCK:if ({_variable.Usage}.{nameof(RequirementResult.Messages)}.Length > 0)");
        writer.Write(
            $"problemDetails{_index}.{nameof(ProblemDetails.Extensions)}[\"errors\"] = {_variable.Usage}.{nameof(RequirementResult.Messages)};");
        writer.FinishBlock();
        writer.Write("BLOCK:else");
        writer.Write(
            $"problemDetails{_index}.{nameof(ProblemDetails.Detail)} = \"Invalid Request\";");
        writer.FinishBlock();
        writer.Write(
            $"await {nameof(HttpHandler.WriteProblems)}(problemDetails{_index}, {_context!.Usage}).ConfigureAwait(false);");
        writer.Write("return;");
        writer.FinishBlock();
        writer.BlankLine();

        Next?.GenerateCode(method, writer);
    }
}
