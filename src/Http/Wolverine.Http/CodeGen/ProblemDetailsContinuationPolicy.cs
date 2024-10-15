using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Wolverine.Configuration;
using Wolverine.Middleware;

namespace Wolverine.Http.CodeGen;

public class ProblemDetailsContinuationPolicy : IContinuationStrategy
{
    public static void WriteProblems(ILogger logger, ProblemDetails details)
    {
        var json = JsonConvert.SerializeObject(details, Formatting.None);
        logger.LogInformation("Found problems with this message: {Problems}", json);
    }
    
    public bool TryFindContinuationHandler(IChain chain, MethodCall call, out Frame? frame)
    {
        var details = call.Creates.FirstOrDefault(x => x.VariableType == typeof(ProblemDetails));

        if (details != null)
        {
            if (chain is HttpChain)
            {
                frame = new MaybeEndWithProblemDetailsFrame(details);
            }
            else
            {
                frame = new MaybeEndHandlerWithProblemDetailsFrame(details);
            }
            
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
        writer.WriteComment("Evaluate whether the processing should stop if there are any problems");
        writer.Write($"BLOCK:if (!(ReferenceEquals({_details.Usage}, {typeof(WolverineContinue).FullNameInCode()}.{nameof(WolverineContinue.NoProblems)})))");
        writer.Write($"await {nameof(HttpHandler.WriteProblems)}({_details.Usage}, {_context!.Usage}).ConfigureAwait(false);");
        writer.Write("return;");
        writer.FinishBlock();
        writer.BlankLine();

        Next?.GenerateCode(method, writer);
    }
}

/// <summary>
/// Used to potentially stop the execution of an Http request
/// based on whether the IResult is a WolverineContinue or something else
/// </summary>
public class MaybeEndHandlerWithProblemDetailsFrame : AsyncFrame
{
    private readonly Variable _details;
    private Variable? _logger;

    public MaybeEndHandlerWithProblemDetailsFrame(Variable details)
    {
        uses.Add(details);
        _details = details;
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _logger = chain.FindVariable(typeof(ILogger));
        yield return _logger;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment("Evaluate whether the processing should stop if there are any problems");
        writer.Write($"BLOCK:if (!(ReferenceEquals({_details.Usage}, {typeof(WolverineContinue).FullNameInCode()}.{nameof(WolverineContinue.NoProblems)})))");
        writer.Write($"{typeof(ProblemDetailsContinuationPolicy).FullNameInCode()}.{nameof(ProblemDetailsContinuationPolicy.WriteProblems)}({_logger.Usage}, {_details.Usage});");
        writer.Write("return;");
        writer.FinishBlock();
        writer.BlankLine();

        Next?.GenerateCode(method, writer);
    }
}