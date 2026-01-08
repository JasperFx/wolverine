using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Microsoft.AspNetCore.Http;
using Wolverine.Configuration;
using Wolverine.Middleware;

namespace Wolverine.Http.CodeGen;

internal class ResultContinuationPolicy : IContinuationStrategy
{
    public bool TryFindContinuationHandler(IChain chain, MethodCall call, out Frame? frame)
    {
        var result = call.Creates.FirstOrDefault(x => x.VariableType.CanBeCastTo<IResult>());

        if (result != null)
        {
            // Preventing double generation
            if (chain.Middleware.OfType<MaybeEndWithResultFrame>().Any(x => ReferenceEquals(x.Result, result)))
            {
                frame = null;
                return false;
            }
            
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
    private Variable? _context;

    public MaybeEndWithResultFrame(Variable result)
    {
        uses.Add(result);
        Result = result;
    }

    public Variable Result { get; }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _context = chain.FindVariable(typeof(HttpContext));
        yield return _context;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        // Super hacky. Cannot for the life of me stop the double generation of "maybe end with IResult", so
        // this.
        if (Next is MaybeEndWithResultFrame next && ReferenceEquals(next.Result, Result))
        {
            Next?.GenerateCode(method, writer);
            return;
        }
        
        writer.WriteComment("Evaluate whether or not the execution should be stopped based on the IResult value");
        writer.Write($"BLOCK:if ({Result.Usage} != null && !({Result.Usage} is {typeof(WolverineContinue).FullNameInCode()}))");
        writer.Write($"await {Result.Usage}.{nameof(IResult.ExecuteAsync)}({_context!.Usage}).ConfigureAwait(false);");
        writer.Write("return;");
        writer.FinishBlock();
        writer.BlankLine();

        Next?.GenerateCode(method, writer);
    }
}