namespace Wolverine.Pulsar;

/// <summary>
/// Transport-wide configuration for the Pulsar integration, returned from
/// <see cref="PulsarTransportExtensions.UsePulsar(Wolverine.WolverineOptions,System.Action{DotPulsar.Abstractions.IPulsarClientBuilder})"/>.
/// Use it to set transport-level defaults that individual endpoints inherit unless they override them.
/// </summary>
public class PulsarConfiguration
{
    private readonly PulsarTransport _transport;

    internal PulsarConfiguration(PulsarTransport transport)
    {
        _transport = transport;
    }

    /// <summary>
    /// Set the transport-wide default dead letter topic applied to every Pulsar endpoint that does
    /// not configure its own via <c>ListenToPulsarTopic(...).DeadLetterQueueing(...)</c>. Per-endpoint
    /// configuration always wins (see <see cref="PulsarEndpoint.EffectiveDeadLetterTopic"/>). Mirrors
    /// the Kafka transport-level dead letter default + per-endpoint override shape.
    /// </summary>
    public PulsarConfiguration DeadLetterQueueing(DeadLetterTopic deadLetterTopic)
    {
        _transport.DeadLetterTopic = deadLetterTopic ?? throw new ArgumentNullException(nameof(deadLetterTopic));
        return this;
    }

    /// <summary>
    /// Set the transport-wide default retry letter topic applied to every Pulsar endpoint that does
    /// not configure its own. Per-endpoint configuration always wins (see
    /// <see cref="PulsarEndpoint.EffectiveRetryLetterTopic"/>).
    /// </summary>
    public PulsarConfiguration RetryLetterQueueing(RetryLetterTopic retryLetterTopic)
    {
        _transport.RetryLetterTopic = retryLetterTopic ?? throw new ArgumentNullException(nameof(retryLetterTopic));
        return this;
    }
}
