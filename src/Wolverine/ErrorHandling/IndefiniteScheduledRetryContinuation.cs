// // Copyright (c) woksin-org. All rights reserved.
// // Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Diagnostics;
using Wolverine.Runtime;

namespace Wolverine.ErrorHandling;

internal class IndefiniteScheduledRetryContinuation : IContinuation, IContinuationSource
{
    private readonly ScheduledRetryContinuation _continuation;
    private readonly FailureRule _failureRule;
    private readonly CancellationToken _cancellationToken;

    public IndefiniteScheduledRetryContinuation(ScheduledRetryContinuation continuation, FailureRule failureRule, CancellationToken cancellationToken)
    {
        _continuation = continuation;
        _failureRule = failureRule;
        _cancellationToken = cancellationToken;
    }

    public TimeSpan Delay => _continuation.Delay;

    public ValueTask ExecuteAsync(IEnvelopeLifecycle lifecycle, IWolverineRuntime runtime, DateTimeOffset now,
        Activity? activity)
    {
        if (!_cancellationToken.IsCancellationRequested)
        {
            _failureRule.AddSlot(this);
        }
        return _continuation.ExecuteAsync(lifecycle, runtime, now, activity);
    }

    public string Description => ToString();

    public IContinuation Build(Exception ex, Envelope envelope)
    {
        return this;
    }

    public override string ToString()
    {
        return $"Schedule Indefinite Retry in {Delay.TotalSeconds} seconds";
    }

    protected bool Equals(IndefiniteScheduledRetryContinuation other)
    {
        return Delay.Equals(other.Delay);
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

        return Equals((IndefiniteScheduledRetryContinuation)obj);
    }

    public override int GetHashCode()
    {
        return Delay.GetHashCode();
    }
}