using Wolverine.Runtime;

namespace Wolverine.ErrorHandling;

/// <summary>
/// Collection of failure handling policies for outgoing message send failures.
/// Unlike handler failure policies, unmatched exceptions return null
/// so the existing retry/circuit-breaker behavior is preserved.
/// </summary>
public class SendingFailurePolicies : IWithFailurePolicies
{
    /// <summary>
    /// Collection of error handling policies for exception handling during the sending of a message
    /// </summary>
    public FailureRuleCollection Failures { get; } = new();

    /// <summary>
    /// Determine the continuation action for a sending failure.
    /// Returns null if no rule matches, allowing the existing retry/circuit-breaker
    /// behavior to proceed unchanged.
    /// </summary>
    public IContinuation? DetermineAction(Exception exception, Envelope envelope)
    {
        // FailureRule.TryCreateContinuation uses Attempts for slot matching.
        // For sending failures, we use SendAttempts instead.
        var savedAttempts = envelope.Attempts;
        envelope.Attempts = envelope.SendAttempts;

        try
        {
            foreach (var rule in Failures)
            {
                if (rule.TryCreateContinuation(exception, envelope, out var continuation))
                {
                    return continuation;
                }
            }

            return null;
        }
        finally
        {
            envelope.Attempts = savedAttempts;
        }
    }

    /// <summary>
    /// Combine this policy set with a parent (global) policy set.
    /// Local rules take priority over parent rules.
    /// </summary>
    public SendingFailurePolicies CombineWith(SendingFailurePolicies parent)
    {
        var combined = new SendingFailurePolicies();

        // Local rules first (higher priority)
        foreach (var rule in Failures)
        {
            combined.Failures.Add(rule);
        }

        // Then parent rules
        foreach (var rule in parent.Failures)
        {
            combined.Failures.Add(rule);
        }

        return combined;
    }

    internal bool HasAnyRules => Failures.Any();
}
