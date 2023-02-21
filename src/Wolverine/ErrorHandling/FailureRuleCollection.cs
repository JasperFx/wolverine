using System;
using System.Collections;
using System.Collections.Generic;
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

    internal IContinuation DetermineExecutionContinuation(Exception e, Envelope envelope)
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

    internal RetryInlineContinuation? TryFindInlineContinuation(Exception e, Envelope envelope)
    {
        foreach (var rule in _rules)
        {
            if (!rule.TryCreateContinuation(e, envelope, out var continuation))
            {
                continue;
            }

            if (continuation is RetryInlineContinuation retry)
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

    public IEnumerator<FailureRule> GetEnumerator()
    {
        return _rules.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }
}