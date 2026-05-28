using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using Wolverine.Configuration;

namespace Wolverine.Middleware;

public static class ContinuationHandling
{
    public static readonly string Continuations = "CONTINUATIONS";

    public static List<IContinuationStrategy> ContinuationStrategies(this GenerationRules rules)
    {
        if (rules.Properties.TryGetValue(Continuations, out var raw) &&
            raw is List<IContinuationStrategy> list)
        {
            return list;
        }

        return [new HandlerContinuationPolicy(), new SimpleValidationContinuationPolicy(), new RequirementResultContinuationPolicy()];
    }

    /// <summary>
    ///     The currently known strategy for code generating transaction middleware
    /// </summary>
    public static void AddContinuationStrategy<T>(this GenerationRules rules) where T : IContinuationStrategy, new()
    {
        if (rules.Properties.TryGetValue(Continuations, out var raw) && raw is List<IContinuationStrategy> list)
        {
            if (!list.OfType<T>().Any())
            {
                list.Add(new T());
            }
        }
        else
        {
            list =
            [
                new HandlerContinuationPolicy(),
                new SimpleValidationContinuationPolicy(),
                new RequirementResultContinuationPolicy(),
                new T()
            ];
            rules.Properties[Continuations] = list;
        }
    }

    public static bool TryFindContinuationHandler(this GenerationRules rules, IChain chain, MethodCall call,
        out Frame? frame)
    {
        var strategies = rules.ContinuationStrategies();
        foreach (var strategy in strategies)
        {
            // GH-2221: strategies that need to consult per-host state (e.g. the custom-Result
            // registry that ResultTypeContinuationPolicy reads) implement the opt-in
            // IRulesAwareContinuationStrategy overload and receive `rules`. Existing strategies
            // continue to use the rules-free overload; this is a non-breaking extension.
            if (strategy is IRulesAwareContinuationStrategy rulesAware)
            {
                if (rulesAware.TryFindContinuationHandler(chain, call, rules, out frame))
                {
                    return true;
                }

                continue;
            }

            if (strategy.TryFindContinuationHandler(chain, call, out frame))
            {
                return true;
            }
        }

        frame = null;
        return false;
    }
}

public interface IContinuationStrategy
{
    bool TryFindContinuationHandler(IChain chain, MethodCall call, out Frame? frame);
}

/// <summary>
/// Opt-in extension of <see cref="IContinuationStrategy" /> for strategies that need read access
/// to <see cref="GenerationRules" /> to consult per-host state (e.g. the custom-Result-type
/// registry that <c>ResultTypeContinuationPolicy</c> reads). Implementing this interface causes
/// the dispatcher to call the rules-aware overload instead of the rules-free one. See GH-2221.
/// </summary>
public interface IRulesAwareContinuationStrategy : IContinuationStrategy
{
    bool TryFindContinuationHandler(IChain chain, MethodCall call, GenerationRules rules, out Frame? frame);
}

internal class HandlerContinuationPolicy : IContinuationStrategy
{
    public bool TryFindContinuationHandler(IChain chain, MethodCall call, out Frame? frame)
    {
        if (call.CreatesNewOf<HandlerContinuation>())
        {
            frame = new HandlerContinuationFrame(call);
            return true;
        }

        frame = null;
        return false;
    }
}