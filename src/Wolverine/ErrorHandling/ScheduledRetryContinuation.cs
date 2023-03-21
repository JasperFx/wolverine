using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Wolverine.Runtime;

namespace Wolverine.ErrorHandling;

internal class ScheduledRetryContinuation : IContinuation, IContinuationSource
{
    private readonly TimeSpan _delay;

    public ScheduledRetryContinuation(TimeSpan delay)
    {
        _delay = delay;
    }

    public TimeSpan Delay => _delay;

    public ValueTask ExecuteAsync(IEnvelopeLifecycle lifecycle, IWolverineRuntime runtime, DateTimeOffset now,
        Activity? activity)
    {
        var scheduledTime = now.Add(_delay);

        return new ValueTask(lifecycle.ReScheduleAsync(scheduledTime));
    }

    public string Description => ToString();

    public IContinuation Build(Exception ex, Envelope envelope)
    {
        return this;
    }

    public override string ToString()
    {
        return $"Schedule Retry in {_delay.TotalSeconds} seconds";
    }

    protected bool Equals(ScheduledRetryContinuation other)
    {
        return _delay.Equals(other._delay);
    }

    public override bool Equals(object? obj)
    {
        if (ReferenceEquals(null, obj))
        {
            return false;
        }

        if (ReferenceEquals(this, obj))
        {
            return true;
        }

        if (obj.GetType() != GetType())
        {
            return false;
        }

        return Equals((ScheduledRetryContinuation)obj);
    }

    public override int GetHashCode()
    {
        return _delay.GetHashCode();
    }
}