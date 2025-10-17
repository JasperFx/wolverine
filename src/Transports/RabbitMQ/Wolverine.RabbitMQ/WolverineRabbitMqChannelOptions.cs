namespace Wolverine.RabbitMQ;

/// <summary>
///     Customizable options for creating a Rabbit MQ channel
/// </summary>
public class WolverineRabbitMqChannelOptions
{
    /// <summary>
    ///     Enable publisher confirmations. Off by default.
    /// </summary>
    public bool PublisherConfirmationsEnabled { get; set; } = false;

    /// <summary>
    ///     Enable tracking of publisher confirmations. Off by default.
    /// </summary>
    public bool PublisherConfirmationTrackingEnabled { get; set; } = false;

    /// <summary>
    ///     The consumer dispatch concurrency. 1 by default.
    /// </summary>
    public ushort? ConsumerDispatchConcurrency { get; set; } = 1;
}