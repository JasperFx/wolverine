using System.Collections;
using Wolverine.ErrorHandling.Matches;
using Wolverine.Runtime;

namespace Wolverine.ErrorHandling;

public class FailureRuleCollection : IEnumerable<FailureRule>
{
    private readonly List<FailureRule> _rules = new();

    /// <summary>
    ///     Maximum number of attempts allowed for this message type
    /// </summary>
    public int? MaximumAttempts { get; set; }

    public IEnumerator<FailureRule> GetEnumerator()
    {
        return _rules.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    internal FailureRuleCollection CombineRules(FailureRuleCollection parent)
    {
        var combined = new FailureRuleCollection();
        combined._rules.AddRange(combine(parent));
        combined.MaximumAttempts = MaximumAttempts ?? parent.MaximumAttempts ?? 3;

        return combined;
    }

    private IEnumerable<FailureRule> combine(FailureRuleCollection parent)
    {
        foreach (var rule in _rules) yield return rule;

        if (MaximumAttempts.HasValue)
        {
            yield return BuildRequeueRuleForMaximumAttempts(MaximumAttempts.Value);
        }

        foreach (var rule in parent._rules) yield return rule;

        if (parent.MaximumAttempts.HasValue)
        {
            yield return BuildRequeueRuleForMaximumAttempts(parent.MaximumAttempts.Value);
        }
    }

    /// <summary>
    /// GH-4060. Does anything in this collection route a failure back to the listener for redelivery? Both the
    /// explicit Requeue()/RequeueIndefinitely()/PauseThenRequeue() rules and the requeue ladder that
    /// <see cref="MaximumAttempts"/> synthesizes in <see cref="combine"/> count.
    /// </summary>
    internal bool AnyRequeuePolicies()
    {
        return MaximumAttempts.HasValue || _rules.Any(x => x.AnyRequeueContinuations());
    }

    /// <summary>
    /// GH-5556. What a chain does in place of moving the message to the error queue once its retries are used up:
    /// a saga's compensation. Set on the combined rules of one chain only, never on the global rules
    /// </summary>
    internal Func<MoveToErrorQueue, Envelope, IContinuation>? InsteadOfErrorQueue { get; set; }

    internal IContinuation DetermineExecutionContinuation(Exception e, Envelope envelope)
    {
        var continuation = determineExecutionContinuation(e, envelope);

        // GH-5556: a terminal failure skips the retries and goes straight to what comes after them
        if (TerminalFailures.IsTerminal(e) && TerminalFailures.Retries(continuation))
        {
            continuation = new MoveToErrorQueue(e);
        }

        if (InsteadOfErrorQueue != null && continuation is MoveToErrorQueue moveToErrorQueue)
        {
            continuation = InsteadOfErrorQueue(moveToErrorQueue, envelope);
        }

        return continuation;
    }

    private IContinuation determineExecutionContinuation(Exception e, Envelope envelope)
    {
        foreach (var rule in _rules)
        {
            if (rule.TryCreateContinuation(e, envelope, out var continuation))
            {
                return continuation;
            }
        }

        return new MoveToErrorQueue(e);
    }

    internal IInlineContinuation? TryFindInlineContinuation(Exception e, Envelope envelope)
    {
        // GH-5556: a terminal failure is never retried, inline or not
        if (TerminalFailures.IsTerminal(e)) return null;

        foreach (var rule in _rules)
        {
            if (!rule.TryCreateContinuation(e, envelope, out var continuation))
            {
                continue;
            }

            if (continuation is CompositeContinuation composite)
            {
                foreach (var inner in composite.Inner)
                {
                    if (inner is IInlineContinuation inline) return inline;
                }
                
                continue;
            }

            if (continuation is IInlineContinuation retry)
            {
                return retry;
            }
        }

        return null;
    }

    internal static FailureRule BuildRequeueRuleForMaximumAttempts(int maximumAttempts)
    {
        var rule = new FailureRule(new AlwaysMatches());
        for (var i = 0; i < maximumAttempts - 1; i++)
        {
            rule.AddSlot(RequeueContinuation.Instance);
        }

        return rule;
    }

    internal void Add(FailureRule rule)
    {
        _rules.Add(rule);
    }
}