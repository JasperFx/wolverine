using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Wolverine.Middleware;

namespace Wolverine.Http.CodeGen;

internal class ProblemDetailsContinuationPolicy : IContinuationStrategy
{
    public bool TryFindContinuationHandler(MethodCall call, out Frame? frame)
    {
        var details = call.Creates.FirstOrDefault(x => x.VariableType == typeof(ProblemDetails));

        if (details != null)
        {
            frame = new MaybeEndWithProblemDetailsFrame(details);
            return true;
        }

        frame = null;
        return false;
    }
}

/// <summary>
/// Used to potentially stop the execution of an Http request
/// based on whether the IResult is a WolverineContinue or something else
/// </summary>
internal class MaybeEndWithProblemDetailsFrame : AsyncFrame
{
    private readonly Variable _details;
    private Variable? _context;

    public MaybeEndWithProblemDetailsFrame(Variable details)
    {
        uses.Add(details);
        _details = details;
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _context = chain.FindVariable(typeof(HttpContext));
        yield return _context;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.Write($"BLOCK:if (!(ReferenceEquals({_details.Usage}, {typeof(WolverineContinue).FullNameInCode()}.{nameof(WolverineContinue.NoProblems)})))");
        writer.Write($"await Microsoft.AspNetCore.Http.Results.Problem({_details.Usage}).{nameof(IResult.ExecuteAsync)}({_context!.Usage}).ConfigureAwait(false);");
        writer.Write("return;");
        writer.FinishBlock();

        Next?.GenerateCode(method, writer);
    }
}