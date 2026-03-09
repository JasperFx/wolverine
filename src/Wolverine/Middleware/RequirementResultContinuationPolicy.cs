using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;

namespace Wolverine.Middleware;

/// <summary>
/// Continuation strategy that detects Validate/ValidateAsync methods returning
/// RequirementResult and generates appropriate validation handling code.
/// If Branch == Continue, processing continues. If Branch == Stop, messages are
/// logged and the handler is aborted.
/// </summary>
public class RequirementResultContinuationPolicy : IContinuationStrategy
{
    /// <summary>
    /// Helper used by generated code to log requirement result messages and return
    /// whether processing should stop.
    /// </summary>
    public static bool ShouldStop(ILogger logger, RequirementResult result)
    {
        if (result.Branch == HandlerContinuation.Continue) return false;

        if (result.Messages.Length > 0)
        {
            foreach (var message in result.Messages)
            {
                logger.LogWarning("Validation failure: {ValidationMessage}", message);
            }
        }
        else
        {
            logger.LogWarning("Validation failure: Invalid Request");
        }

        return true;
    }

    public bool TryFindContinuationHandler(IChain chain, MethodCall call, out Frame? frame)
    {
        if (call.Method.Name != "Validate" && call.Method.Name != "ValidateAsync")
        {
            frame = null;
            return false;
        }

        var variable = FindRequirementResultVariable(call);
        if (variable == null)
        {
            frame = null;
            return false;
        }

        frame = chain.CreateRequirementResultFrame(variable);
        return frame != null;
    }

    internal static Variable? FindRequirementResultVariable(MethodCall call)
    {
        foreach (var variable in call.Creates)
        {
            if (variable.VariableType == typeof(RequirementResult))
            {
                return variable;
            }
        }

        return null;
    }
}

/// <summary>
/// Frame that generates validation code for message handlers using RequirementResult.
/// Logs validation messages and returns if Branch == Stop.
/// </summary>
internal class RequirementResultHandlerFrame : SyncFrame
{
    private static int _count;
    private readonly Variable _variable;
    private Variable? _logger;

    public RequirementResultHandlerFrame(Variable variable)
    {
        _variable = variable;
        _variable.OverrideName(_variable.Usage + ++_count);
        uses.Add(_variable);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _logger = chain.FindVariable(typeof(ILogger));
        yield return _logger;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment("Check RequirementResult and abort if Branch == Stop");
        writer.Write(
            $"BLOCK:if ({typeof(RequirementResultContinuationPolicy).FullNameInCode()}.{nameof(RequirementResultContinuationPolicy.ShouldStop)}({_logger!.Usage}, {_variable.Usage}))");

        if (method.AsyncMode == AsyncMode.AsyncTask)
        {
            writer.Write("return;");
        }
        else
        {
            writer.Write($"return {typeof(Task).FullNameInCode()}.{nameof(Task.CompletedTask)};");
        }

        writer.FinishBlock();
        writer.BlankLine();

        Next?.GenerateCode(method, writer);
    }
}
