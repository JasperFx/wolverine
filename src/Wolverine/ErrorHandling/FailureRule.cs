using System.Collections;
using Wolverine.ErrorHandling.Matches;
using Wolverine.Runtime;

namespace Wolverine.ErrorHandling;

public class FailureRule : IEnumerable<FailureSlot>
{
    private readonly List<FailureSlot> _slots = new();

    public FailureRule(IExceptionMatch match)
    {
        Match = match;
    }

    public FailureSlot this[int attempt] => _slots[attempt - 1];

    public IExceptionMatch Match { get; }
    internal IContinuationSource? InfiniteSource { get; set; }

    public IEnumerator<FailureSlot> GetEnumerator()
    {
        return _slots.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    public bool TryCreateContinuation(Exception ex, Envelope env, out IContinuation continuation)
    {
        if (Match.Matches(ex))
        {
            if (env.Attempts == 0)
            {
                env.Attempts = 1;
            }

            var slot = _slots.FirstOrDefault(x => x.Attempt == env.Attempts);

            if (slot?.Build(ex, env) is { } fromSlot)
            {
                continuation = fromSlot;
                return true;
            }

            if (InfiniteSource?.Build(ex, env) is { } fromInfiniteSource)
            {
                continuation = fromInfiniteSource;
                return true;
            }

            // GH-4079. Every source this rule could offer for this attempt *declined* the envelope, which is not
            // the same thing as the rule running out of attempts. Report the rule as unhandled so that
            // FailureRuleCollection moves on to the next rule -- otherwise a globally registered rule
            // that only speaks for one transport would swallow every user-configured policy behind it.
            // See IContinuationSource.Build.
            if (slot != null || InfiniteSource != null)
            {
                continuation = NullContinuation.Instance;
                return false;
            }

            // No slot for this attempt and no infinite source: this rule's attempts are exhausted.
            continuation = new MoveToErrorQueue(ex);
            return true;
        }

        continuation = NullContinuation.Instance;
        return false;
    }

    public FailureSlot AddSlot(IContinuationSource source)
    {
        var attempt = _slots.Count + 1;
        var slot = new FailureSlot(attempt, source);
        _slots.Add(slot);

        return slot;
    }

    public override string ToString()
    {
        var parts = new List<string>(_slots.Count + 1);

        foreach (var slot in _slots)
        {
            parts.Add($"attempt {slot.Attempt}: {slot.Describe()}");
        }

        if (InfiniteSource != null)
        {
            var prefix = _slots.Count > 0 ? "then repeat" : "repeat";
            parts.Add($"{prefix}: {InfiniteSource.Description}");
        }

        var actions = parts.Count > 0 ? string.Join("; ", parts) : "no action";
        return $"On {Match.Description} \u2014 {actions}";
    }
}