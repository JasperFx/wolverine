using JasperFx;
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

internal class ProblemDetailsFromMiddleware : IHttpPolicy
{
    public void Apply(IReadOnlyList<HttpChain> chains, GenerationRules rules, IServiceContainer container)
    {
        foreach (var chain in chains)
        {
            foreach (var methodCall in chain.Middleware.OfType<MethodCall>().ToArray())
            {
                foreach (var details in methodCall.Creates.Where(x => x.VariableType == typeof(ProblemDetails) && !x.Properties.ContainsKey("checked")))
                {
                    var index = chain.Middleware.IndexOf(methodCall);
                    chain.Middleware.Insert(index + 1, new MaybeEndWithProblemDetailsFrame(details));
                    details.Properties["checked"] = true;
                }
            }
        }
    }
}

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


        if (details != null && !details.Properties.ContainsKey("checked"))
        {
            if (chain.Middleware.OfType<IMaybeEndWithProblemDetails>().Any(x => ReferenceEquals(x.Details, details)))
            {
                frame = null;
                return false;
            }
            
            details.Properties["checked"] = true;
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

internal interface IMaybeEndWithProblemDetails
{
    Variable Details { get; }
}

/// <summary>
/// Used to potentially stop the execution of an Http request
/// based on whether the IResult is a WolverineContinue or something else
/// </summary>
internal class MaybeEndWithProblemDetailsFrame : AsyncFrame, IMaybeEndWithProblemDetails
{
    private Variable? _context;

    public MaybeEndWithProblemDetailsFrame(Variable details)
    {
        uses.Add(details);
        Details = details;
    }

    public Variable Details { get; }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _context = chain.FindVariable(typeof(HttpContext));
        yield return _context;
    }
    
    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment("Evaluate whether the processing should stop if there are any problems");
        writer.Write($"BLOCK:if (!(ReferenceEquals({Details.Usage}, {typeof(WolverineContinue).FullNameInCode()}.{nameof(WolverineContinue.NoProblems)})))");
        writer.Write($"await {nameof(HttpHandler.WriteProblems)}({Details.Usage}, {_context!.Usage}).ConfigureAwait(false);");
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
public class MaybeEndHandlerWithProblemDetailsFrame : AsyncFrame, IMaybeEndWithProblemDetails
{
    private Variable? _logger;

    public MaybeEndHandlerWithProblemDetailsFrame(Variable details)
    {
        uses.Add(details);
        Details = details;
    }

    public Variable Details { get; }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _logger = chain.FindVariable(typeof(ILogger));
        yield return _logger;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment("Evaluate whether the processing should stop if there are any problems");
        writer.Write($"BLOCK:if (!(ReferenceEquals({Details.Usage}, {typeof(WolverineContinue).FullNameInCode()}.{nameof(WolverineContinue.NoProblems)})))");
        writer.Write($"{typeof(ProblemDetailsContinuationPolicy).FullNameInCode()}.{nameof(ProblemDetailsContinuationPolicy.WriteProblems)}({_logger.Usage}, {Details.Usage});");
        writer.Write("return;");
        writer.FinishBlock();
        writer.BlankLine();

        Next?.GenerateCode(method, writer);
    }
}