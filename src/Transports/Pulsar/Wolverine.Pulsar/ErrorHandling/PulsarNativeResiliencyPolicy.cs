using Wolverine.Configuration;
using Wolverine.ErrorHandling;
using Wolverine.ErrorHandling.Matches;

namespace Wolverine.Pulsar.ErrorHandling;

public class PulsarNativeResiliencyPolicy : IWolverinePolicy
{
    public void Apply(WolverineOptions options)
    {
        var rule = new FailureRule(new AlwaysMatches());

        rule.AddSlot(new PulsarNativeContinuationSource());

        // Set the same source as the InfiniteSource to handle all other attempts
        rule.InfiniteSource = new PulsarNativeContinuationSource();

        // Add this rule to the global failure collection
        // This ensures it will be checked before other rules
        options.Policies.Failures.Add(rule);
    }
}