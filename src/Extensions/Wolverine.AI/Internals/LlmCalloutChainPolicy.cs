using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.Core.Reflection;
using Wolverine.Configuration;
using Wolverine.ErrorHandling;
using Wolverine.Persistence;
using Wolverine.Runtime.Handlers;

namespace Wolverine.AI.Internals;

/// <summary>
/// Applies the callout chain's error handling and, when asked for, its deduplication requirement.
/// </summary>
/// <remarks>
/// A policy rather than attributes on <see cref="LlmCalloutHandler" /> because both are configurable:
/// the retry schedule comes from <see cref="LlmCalloutOptions.RetryCooldowns" /> and deduplication is
/// opt in, and neither can be spelled as a constant in an attribute argument.
/// </remarks>
internal class LlmCalloutChainPolicy : IHandlerPolicy
{
    private readonly LlmCalloutOptions _options;

    public LlmCalloutChainPolicy(LlmCalloutOptions options)
    {
        _options = options;
    }

    public void Apply(IReadOnlyList<HandlerChain> chains, GenerationRules rules, IServiceContainer container)
    {
        foreach (var chain in chains.Where(x => x.MessageType == typeof(LlmCallout)))
        {
            // Both of these are terminal on purpose. A prompt the model cannot answer in the requested
            // shape produces the identical unusable answer on every attempt, and a callout that blows the
            // budget blows it again on every attempt -- retrying either one is the runaway spend and the
            // pointless bill that the dead letter queue exists to stop.
            chain.OnException<LlmBudgetExceededException>().MoveToErrorQueue();
            chain.OnException<LlmCalloutException>().MoveToErrorQueue();

            if (_options.RetryCooldowns.Any())
            {
                chain.OnException<Exception>().RetryWithCooldown(_options.RetryCooldowns);
            }

            if (_options.DeduplicateCallouts)
            {
                // Required = false because a mixed stream is the normal case: only the republish prone
                // sources -- a projection's RaiseSideEffects, say -- have a natural logical key, and
                // refusing every unkeyed callout would break the ordinary handler pattern outright.
                chain.Deduplication = new DeduplicationRequirement { Required = false };
            }
        }
    }
}
