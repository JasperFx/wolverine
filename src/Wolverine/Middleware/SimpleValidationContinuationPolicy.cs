using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;

namespace Wolverine.Middleware;

/// <summary>
/// Continuation strategy that detects Validate/ValidateAsync methods returning
/// IEnumerable&lt;string&gt;, string[], Task&lt;string[]&gt;, or ValueTask&lt;string[]&gt;
/// and generates appropriate validation handling code.
/// </summary>
public class SimpleValidationContinuationPolicy : IContinuationStrategy
{
    private static readonly Type[] ValidReturnTypes =
    [
        typeof(IEnumerable<string>),
        typeof(string[])
    ];

    /// <summary>
    /// Helper used by generated code to log validation messages and return a boolean
    /// indicating whether there are any validation failures.
    /// </summary>
    public static bool LogValidationMessages(ILogger logger, IEnumerable<string> messages)
    {
        var hasMessages = false;
        foreach (var message in messages)
        {
            hasMessages = true;
            logger.LogWarning("Validation failure: {ValidationMessage}", message);
        }

        return hasMessages;
    }

    public bool TryFindContinuationHandler(IChain chain, MethodCall call, out Frame? frame)
    {
        // Only apply to methods named Validate or ValidateAsync
        if (call.Method.Name != "Validate" && call.Method.Name != "ValidateAsync")
        {
            frame = null;
            return false;
        }

        var variable = FindStringEnumerableVariable(call);
        if (variable == null)
        {
            frame = null;
            return false;
        }

        frame = chain.CreateSimpleValidationFrame(variable);
        return frame != null;
    }

    internal static Variable? FindStringEnumerableVariable(MethodCall call)
    {
        foreach (var variable in call.Creates)
        {
            if (IsStringEnumerable(variable.VariableType))
            {
                return variable;
            }
        }

        return null;
    }

    internal static bool IsStringEnumerable(Type type)
    {
        if (type == typeof(IEnumerable<string>)) return true;
        if (type == typeof(string[])) return true;
        if (type == typeof(List<string>)) return true;

        return false;
    }
}

/// <summary>
/// Frame that generates validation code for message handlers.
/// Logs validation messages and returns if any are found.
/// </summary>
internal class SimpleValidationHandlerFrame : SyncFrame
{
    private static int _count;
    private readonly Variable _variable;
    private Variable? _logger;

    public SimpleValidationHandlerFrame(Variable variable)
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
        writer.WriteComment("Check for any simple validation messages and abort if any exist");
        writer.Write(
            $"BLOCK:if ({typeof(SimpleValidationContinuationPolicy).FullNameInCode()}.{nameof(SimpleValidationContinuationPolicy.LogValidationMessages)}({_logger!.Usage}, {_variable.Usage}))");

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
