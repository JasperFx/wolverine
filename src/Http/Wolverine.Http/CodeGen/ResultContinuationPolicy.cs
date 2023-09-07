using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Microsoft.AspNetCore.Http;
using Wolverine.Middleware;

namespace Wolverine.Http.CodeGen;

internal class ResultContinuationPolicy : IContinuationStrategy
{
    public bool TryFindContinuationHandler(MethodCall call, out Frame? frame)
    {
        var result = call.Creates.FirstOrDefault(x => x.VariableType.CanBeCastTo<IResult>());

        if (result != null)
        {
            frame = new MaybeEndWithResultFrame(result);
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
public class MaybeEndWithResultFrame : AsyncFrame
{
    private readonly Variable _result;
    private Variable? _context;

    public MaybeEndWithResultFrame(Variable result)
    {
        uses.Add(result);
        _result = result;
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _context = chain.FindVariable(typeof(HttpContext));
        yield return _context;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment("Evaluate whether or not the execution should be stopped based on the IResult value");
        writer.Write($"BLOCK:if (!({_result.Usage} is {typeof(WolverineContinue).FullNameInCode()}))");
        writer.Write($"await {_result.Usage}.{nameof(IResult.ExecuteAsync)}({_context!.Usage}).ConfigureAwait(false);");
        writer.Write("return;");
        writer.FinishBlock();
        writer.BlankLine();

        Next?.GenerateCode(method, writer);
    }
}