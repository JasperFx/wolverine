using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Wolverine.Http.CodeGen;

/// <summary>
/// Frame that generates validation code for HTTP endpoints.
/// Creates a ProblemDetails with status 400 and writes it to the response if validation messages exist.
/// </summary>
internal class SimpleValidationHttpFrame : AsyncFrame
{
    private static int _count;
    private readonly Variable _variable;
    private Variable? _context;

    public SimpleValidationHttpFrame(Variable variable)
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
        writer.WriteComment("Check for any simple validation messages and abort with ProblemDetails if any exist");
        writer.Write(
            $"var validationMessages{_count} = {_variable.Usage}.ToArray();");
        writer.Write(
            $"BLOCK:if (validationMessages{_count}.Length > 0)");
        writer.Write(
            $"var problemDetails{_count} = new {typeof(ProblemDetails).FullNameInCode()}();");
        writer.Write(
            $"problemDetails{_count}.{nameof(ProblemDetails.Status)} = 400;");
        writer.Write(
            $"problemDetails{_count}.{nameof(ProblemDetails.Title)} = \"Validation failed\";");
        writer.Write(
            $"problemDetails{_count}.{nameof(ProblemDetails.Extensions)}[\"errors\"] = validationMessages{_count};");
        writer.Write(
            $"await {nameof(HttpHandler.WriteProblems)}(problemDetails{_count}, {_context!.Usage}).ConfigureAwait(false);");
        writer.Write("return;");
        writer.FinishBlock();
        writer.BlankLine();

        Next?.GenerateCode(method, writer);
    }
}
