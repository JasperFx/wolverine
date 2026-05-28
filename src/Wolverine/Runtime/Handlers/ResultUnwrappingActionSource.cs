using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Middleware;
using Wolverine.Runtime;

namespace Wolverine.Runtime.Handlers;

/// <summary>
/// GH-2221 seam 3 — runtime helper invoked from generated code. Looks up the registration for a
/// result value's runtime type, logs failures via the same path <c>ResultTypeContinuationPolicy</c>
/// uses, and returns the success payload (or <c>null</c> when the result represents a failure or
/// has no payload). A return of <c>null</c> signals the caller to suppress the cascade.
/// </summary>
public static class ResultUnwrapper
{
    /// <summary>
    /// Look up the registration for the runtime type of <paramref name="result" />. On failure,
    /// log each error via the registration's <c>ErrorsFrom</c> projection and return <c>null</c>
    /// — the caller suppresses the cascade. On success, return the success payload (which may
    /// itself be <c>null</c> for a non-generic Result type, in which case there's nothing to
    /// cascade either).
    /// </summary>
    public static object? UnwrapOrLog(ResultTypeRegistry registry, ILogger logger, object? result)
    {
        if (result is null) return null;

        var registration = registry?.TryFind(result.GetType());
        if (registration == null)
        {
            // Not a registered Result type — pass through unchanged so this helper can be safely
            // placed on chains that may or may not actually produce a Result at runtime.
            return result;
        }

        if (registration.ShouldStop(result))
        {
            var emitted = false;
            var errors = registration.Errors(result);
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

            return null;
        }

        return registration.Unwrap(result);
    }

    /// <summary>
    /// One-call helper used directly by generated code from <see cref="ResultUnwrapAndCascadeFrame" />.
    /// Combines <see cref="UnwrapOrLog" /> with the cascade so the emitted source is a single
    /// awaited call against the concrete <see cref="MessageContext" /> the runtime always provides
    /// (the public <c>IMessageContext</c> interface doesn't expose <c>EnqueueCascadingAsync</c>).
    /// </summary>
    public static async Task UnwrapAndCascadeAsync(
        ResultTypeRegistry registry, ILogger logger, MessageContext context, object? result)
    {
        var unwrapped = UnwrapOrLog(registry, logger, result);
        if (unwrapped != null && context != null)
        {
            await context.EnqueueCascadingAsync(unwrapped).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// GH-2221 seam 3. Replaces the chain's default <see cref="IReturnVariableActionSource" /> when
/// the handler returns a registered Result type. On success the inner payload is cascaded as if
/// the handler had returned <c>T</c> directly; on failure errors are logged and nothing is
/// cascaded.
/// </summary>
internal sealed class ResultUnwrappingActionSource : IReturnVariableActionSource
{
    public IReturnVariableAction Build(IChain chain, Variable variable)
        => new ResultUnwrappingAction(variable);
}

internal sealed class ResultUnwrappingAction : IReturnVariableAction
{
    private readonly Variable _result;

    public ResultUnwrappingAction(Variable result)
    {
        _result = result;
    }

    public string Description => "Unwrap Result<T> + cascade inner value (skip on failure)";

    public IEnumerable<Type> Dependencies()
    {
        yield break;
    }

    public IEnumerable<Frame> Frames()
    {
        yield return new ResultUnwrapAndCascadeFrame(_result);
    }
}

internal sealed class ResultUnwrapAndCascadeFrame : AsyncFrame
{
    private readonly Variable _result;
    private Variable? _registry;
    private Variable? _logger;
    private Variable? _context;

    public ResultUnwrapAndCascadeFrame(Variable result)
    {
        _result = result;
        uses.Add(result);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _registry = chain.FindVariable(typeof(ResultTypeRegistry));
        yield return _registry;

        _logger = chain.FindVariable(typeof(ILogger));
        yield return _logger;

        _context = chain.FindVariable(typeof(MessageContext));
        yield return _context;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment("GH-2221: unwrap a Result<T>-style return; on failure logs + skips the cascade");
        writer.Write(
            $"await {typeof(ResultUnwrapper).FullNameInCode()}.{nameof(ResultUnwrapper.UnwrapAndCascadeAsync)}({_registry!.Usage}, {_logger!.Usage}, {_context!.Usage}, {_result.Usage});");
        writer.BlankLine();

        Next?.GenerateCode(method, writer);
    }
}
