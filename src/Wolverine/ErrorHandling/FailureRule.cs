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
            
            continuation = slot?.Build(ex, env) ?? InfiniteSource?.Build(ex, env) ?? new MoveToErrorQueue(ex);
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