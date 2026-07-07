using JasperFx.Core;
using JasperFx.Core.Reflection;

namespace Wolverine.Tracking;

/// <summary>
///     Tracked-session condition that holds the session open until a minimum number of
///     messages have finished execution for every registered message type. All expectations
///     are AND'ed together within this single condition. Use this when handled messages are
///     published out-of-band from the tracked execution — e.g. a Marten async daemon
///     subscription or projection side effect relaying messages to Wolverine after its page
///     commits — where the tracked session could otherwise observe a momentary lull in
///     activity and complete before every expected message has even been published.
/// </summary>
internal class WaitForExecutionCount : ITrackedCondition
{
    private readonly object _lock = new();
    private readonly List<Expectation> _expectations = new();
    private volatile bool _isCompleted;

    public void ExpectMessage<T>(int count)
    {
        lock (_lock)
        {
            _expectations.Add(new Expectation(typeof(T), m => m is T, count));
            _isCompleted = _expectations.All(x => x.IsSatisfied());
        }
    }

    public void Record(EnvelopeRecord record)
    {
        if (_isCompleted || record.MessageEventType != MessageEventType.ExecutionFinished)
        {
            return;
        }

        var message = record.Envelope?.Message;
        if (message == null)
        {
            return;
        }

        lock (_lock)
        {
            var matchedAny = false;
            foreach (var expectation in _expectations)
            {
                matchedAny = expectation.TryRecord(record.Envelope!.Id, message) || matchedAny;
            }

            if (matchedAny)
            {
                _isCompleted = _expectations.All(x => x.IsSatisfied());
            }
        }
    }

    public bool IsCompleted()
    {
        return _isCompleted;
    }

    public override string ToString()
    {
        lock (_lock)
        {
            return "Wait for message executions: " +
                   _expectations.Select(x => x.ToString()).Join("; ");
        }
    }

    private class Expectation
    {
        private readonly Type _messageType;
        private readonly Func<object, bool> _matches;
        private readonly int _required;

        // Count distinct envelopes so inline retries (multiple ExecutionFinished
        // records for the same envelope) can't satisfy the count prematurely
        private readonly HashSet<Guid> _seen = new();

        public Expectation(Type messageType, Func<object, bool> matches, int required)
        {
            _messageType = messageType;
            _matches = matches;
            _required = required;
        }

        public bool TryRecord(Guid envelopeId, object message)
        {
            if (!_matches(message))
            {
                return false;
            }

            _seen.Add(envelopeId);
            return true;
        }

        public bool IsSatisfied()
        {
            return _seen.Count >= _required;
        }

        public override string ToString()
        {
            return $"{_messageType.FullNameInCode()} executed at least {_required} time(s), saw {_seen.Count}";
        }
    }
}
