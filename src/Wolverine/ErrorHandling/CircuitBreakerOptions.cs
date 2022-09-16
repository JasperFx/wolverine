using System;
using System.Collections.Generic;
using System.Linq;
using Baseline.Dates;
using Wolverine.ErrorHandling.Matches;

namespace Wolverine.ErrorHandling;

/// <summary>
/// Models circuit breaker mechanics to pause the message processing for a single
/// listener when the designated percentage of messages have failed over a tracking period
/// </summary>
public class CircuitBreakerOptions
{
    /// <summary>
    /// The minimum number of messages that should be captured by the circuit breaker
    /// before applying the failure filter. This prevents the circuit breaker from tripping
    /// off on application startup or without enough messages to make an adequate judgement
    /// about the health of the system. The default is 10.
    /// </summary>
    public int MinimumThreshold { get; set; } = 10;

    /// <summary>
    /// The percentage of messages failing that will trip off the circuit breaker
    /// from 1-100. The default is 15.
    /// </summary>
    public int FailurePercentageThreshold { get; set; } = 15;

    /// <summary>
    /// The length of time before the tripped circuit breaker will attempt to restart
    /// the listening agent. The default is 5 minutes
    /// </summary>
    public TimeSpan PauseTime { get; set; } = 5.Minutes();

    /// <summary>
    /// The length of time the circuit breaker keeps statistics
    /// on successes or failures at any one time. The default is 10 minutes
    /// </summary>
    public TimeSpan TrackingPeriod { get; set; } = 10.Minutes();

    /// <summary>
    /// Advanced usage only, default is 250 milliseconds. Sets the maximum time between
    /// evaluating the circuit breaker logic
    /// </summary>
    public TimeSpan SamplingPeriod { get; set; } = 250.Milliseconds();

    private readonly ComplexMatch _match = new();

    /// <summary>
    /// Exclude specified exception type and optional filter from being tracked as message
    /// failures
    /// </summary>
    /// <param name="filter"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public CircuitBreakerOptions Exclude<T>(Func<T, bool>? filter = null) where T : Exception
    {
        _match.Exclude<T>(filter);
        return this;
    }

    /// <summary>
    /// If specified, create an allow list inclusion of certain exception types as failure
    /// </summary>
    /// <param name="filter"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public CircuitBreakerOptions Include<T>(Func<T, bool>? filter = null) where T : Exception
    {
        _match.Include<T>(filter);
        return this;
    }

    internal IExceptionMatch ToExceptionMatch()
    {
        return _match.Reduce();
    }

    internal void AssertValid()
    {
        var messages = validate().ToArray();
        if (messages.Any())
        {
            throw new InvalidCircuitBreakerException(messages);
        }
    }

    private IEnumerable<string> validate()
    {
        if (MinimumThreshold < 0) yield return $"{nameof(MinimumThreshold)} must be greater than 0";

        if (FailurePercentageThreshold <= 1) yield return $"{nameof(FailurePercentageThreshold)} must be at least 1";
        if (FailurePercentageThreshold >= 100) yield return $"{nameof(FailurePercentageThreshold)} must be less than 100";

        // ReSharper disable once ConditionIsAlwaysTrueOrFalse
        if (PauseTime == null) yield return $"{nameof(PauseTime)} cannot be null";

        // ReSharper disable once ConditionIsAlwaysTrueOrFalse
        if (TrackingPeriod == null) yield return $"{nameof(TrackingPeriod)} cannot be null";
        // ReSharper disable once ConditionIsAlwaysTrueOrFalse
        if (SamplingPeriod == null) yield return $"{nameof(SamplingPeriod)} cannot be null";
    }


}
