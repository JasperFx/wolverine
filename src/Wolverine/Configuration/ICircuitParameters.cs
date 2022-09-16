using System;

namespace Wolverine.Configuration;

public interface ICircuitParameters
{
    /// <summary>
    ///     Duration of time to wait before attempting to "ping" a transport
    ///     in an attempt to resume a broken sending circuit
    /// </summary>
    TimeSpan PingIntervalForCircuitResume { get; set; }

    /// <summary>
    ///     How many times outgoing message sending can fail before tripping
    ///     off the circuit breaker functionality. Applies to all transport types
    /// </summary>
    int FailuresBeforeCircuitBreaks { get; set; }

    /// <summary>
    ///     Caps the number of envelopes held in memory for outgoing retries
    ///     if an outgoing transport fails.
    /// </summary>
    int MaximumEnvelopeRetryStorage { get; set; }
}
