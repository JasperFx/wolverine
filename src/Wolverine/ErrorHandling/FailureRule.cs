using System;
using System.Collections.Generic;
using System.Linq;
using Wolverine.ErrorHandling.Matches;
using Wolverine.Runtime;

namespace Wolverine.ErrorHandling;

internal class FailureRule
{
    private readonly IExceptionMatch _match;
    private readonly List<FailureSlot> _slots = new();

    public FailureRule(IExceptionMatch match)
    {
        _match = match;
    }

    public bool TryCreateContinuation(Exception ex, Envelope env, out IContinuation continuation)
    {
        if (_match.Matches(ex))
        {
            if (env.Attempts == 0)
            {
                env.Attempts = 1;
            }

            var slot = _slots.FirstOrDefault(x => x.Attempt == env.Attempts);
            continuation = slot?.Build(ex, env) ?? new MoveToErrorQueue(ex);
            return true;
        }

        continuation = NullContinuation.Instance;
        return false;
    }

    public FailureSlot this[int attempt] => _slots[attempt - 1];

    public FailureSlot AddSlot(IContinuationSource source)
    {
        var attempt = _slots.Count + 1;
        var slot = new FailureSlot(attempt, source);
        _slots.Add(slot);

        return slot;
    }
}
