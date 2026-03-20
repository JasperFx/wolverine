using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Wolverine.Http.CodeGen;

public static class SimpleValidationHttpFrameHelpers
{
    public static bool HasErrors(IEnumerable<string> validationMessages)
    {
        return validationMessages.Any();
    }

    public static ProblemDetails CreateProblemDetails(IEnumerable<string> validationMessages)
    {
        return new()
        {
            Status = 400,
            Title = "Validation failed",
            Extensions =
            {
                ["errors"] = validationMessages.ToArray()
            }
        };
    }

    public static bool HasErrors(ValidationOutcome outcome)
    {
        return !outcome.IsValid();
    }

    public static ProblemDetails CreateProblemDetails(ValidationOutcome outcome)
    {
        var errors = outcome
            .Select(x => x.Key)
            .Distinct()
            .ToDictionary(
                x => x,
                x => outcome
                    .Where(r => r.Key == x)
                    .Select(r => r.ValidationMessage)
                    .ToArray());
        return new ValidationProblemDetails(errors)
        {
            Title = "Validation failed",
        };
    }
}

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
            $"BLOCK:if ({typeof(SimpleValidationHttpFrameHelpers).FullNameInCode()}.{nameof(SimpleValidationHttpFrameHelpers.HasErrors)}({_variable.Usage}))");
        writer.Write(
            $"var problemDetails{_count} = {typeof(SimpleValidationHttpFrameHelpers).FullNameInCode()}.{nameof(SimpleValidationHttpFrameHelpers.CreateProblemDetails)}({_variable.Usage});");
        writer.Write(
            $"await {nameof(HttpHandler.WriteProblems)}(problemDetails{_count}, {_context!.Usage}).ConfigureAwait(false);");
        writer.Write("return;");
        writer.FinishBlock();
        writer.BlankLine();

        Next?.GenerateCode(method, writer);
    }
}
