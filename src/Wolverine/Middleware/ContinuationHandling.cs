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

        return [new HandlerContinuationPolicy()];
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