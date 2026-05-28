using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;

namespace Wolverine.Middleware;

/// <summary>
/// GH-2221 seam 1. Continuation strategy that detects a "Before"-style middleware/filter method
/// whose return type is a registered custom Result type (see
/// <see cref="WolverineOptions.UseResultType{TResult}(System.Func{TResult,bool},System.Func{TResult,object?},System.Func{TResult,System.Collections.Generic.IEnumerable{string}})" />),
/// and emits a frame that early-returns the handler when the result represents a failure / stop.
///
/// Mirrors <see cref="RequirementResultContinuationPolicy" /> structurally — the difference is
/// that the recognized type and the stop predicate come from a user-supplied
/// <see cref="IResultTypeRegistration" /> rather than being hard-coded to
/// <see cref="RequirementResult" />.
/// </summary>
public class ResultTypeContinuationPolicy : IRulesAwareContinuationStrategy
{
    /// <summary>
    /// Helper invoked from generated code. Looks up the registration for the runtime type of
    /// <paramref name="result" />, evaluates <see cref="IResultTypeRegistration.ShouldStop" />,
    /// and on stop logs each error via <see cref="IResultTypeRegistration.Errors" /> at Warning
    /// level before returning <c>true</c>.
    /// </summary>
    public static bool ShouldStop(ResultTypeRegistry registry, ILogger logger, object? result)
    {
        if (result is null) return false;

        var registration = registry?.TryFind(result.GetType());
        if (registration == null) return false;

        if (!registration.ShouldStop(result)) return false;

        var errors = registration.Errors(result);
        var emitted = false;

        if (errors != null)
        {
            foreach (var message in errors)
            {
                if (string.IsNullOrEmpty(message)) continue;
                logger.LogWarning("Result failure: {ResultMessage}", message);
                emitted = true;
            }
        }

        if (!emitted)
        {
            logger.LogWarning("Result failure: {ResultType} returned a stop continuation",
                result.GetType().FullName);
        }

        return true;
    }

    public bool TryFindContinuationHandler(IChain chain, MethodCall call, out Frame? frame)
    {
        // Rules-free overload is unreachable in practice because the dispatcher always calls the
        // rules-aware overload first when the strategy implements IRulesAwareContinuationStrategy.
        // Provide a safe fallback that's effectively a no-op when the registry isn't accessible.
        frame = null;
        return false;
    }

    public bool TryFindContinuationHandler(IChain chain, MethodCall call, GenerationRules rules,
        out Frame? frame)
    {
        if (!MiddlewarePolicy.BeforeMethodNames.Contains(call.Method.Name))
        {
            frame = null;
            return false;
        }

        if (!rules.Properties.TryGetValue(WolverineOptions.ResultTypeRegistryKey, out var raw)
            || raw is not ResultTypeRegistry registry
            || !registry.HasAny)
        {
            frame = null;
            return false;
        }

        var resultVariable = FindResultVariable(call, registry);
        if (resultVariable == null)
        {
            frame = null;
            return false;
        }

        frame = new ResultTypeHandlerFrame(resultVariable);
        return true;
    }

    private static Variable? FindResultVariable(MethodCall call, ResultTypeRegistry registry)
    {
        foreach (var variable in call.Creates)
        {
            if (registry.IsResultType(variable.VariableType))
            {
                return variable;
            }
        }

        return null;
    }
}

/// <summary>
/// Generated code emitted by <see cref="ResultTypeContinuationPolicy" />. Dispatches through
/// <see cref="ResultTypeContinuationPolicy.ShouldStop" /> at runtime — the registry resolves the
/// concrete <see cref="IResultTypeRegistration" /> for the result's runtime type and logs / stops
/// as configured. Boxing the result through <see cref="object" /> at the boundary keeps the
/// emitted code identical regardless of the user's Result library, so the same frame works for
/// every registered type.
/// </summary>
internal class ResultTypeHandlerFrame : SyncFrame
{
    private static int _count;
    private readonly Variable _variable;
    private Variable? _logger;
    private Variable? _registry;

    public ResultTypeHandlerFrame(Variable variable)
    {
        _variable = variable;
        _variable.OverrideName(_variable.Usage + ++_count);
        uses.Add(_variable);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _logger = chain.FindVariable(typeof(ILogger));
        yield return _logger;

        _registry = chain.FindVariable(typeof(ResultTypeRegistry));
        yield return _registry;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment("GH-2221: stop if the middleware Result registered via UseResultType<>() reports failure");
        writer.Write(
            $"BLOCK:if ({typeof(ResultTypeContinuationPolicy).FullNameInCode()}.{nameof(ResultTypeContinuationPolicy.ShouldStop)}({_registry!.Usage}, {_logger!.Usage}, {_variable.Usage}))");

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
