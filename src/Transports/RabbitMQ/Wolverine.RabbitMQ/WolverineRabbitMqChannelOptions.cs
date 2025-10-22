namespace Wolverine.RabbitMQ;

/// <summary>
/// Provides customizable options for configuring the behavior of RabbitMQ channels
/// during their creation process in Wolverine RabbitMQ integration.
/// </summary>
public class WolverineRabbitMqChannelOptions
{
    /// <summary>
    /// Determines whether publisher confirmations are enabled for RabbitMQ channels.
    /// Defaults to false. When enabled, the system waits for acknowledgments from the
    /// RabbitMQ broker to confirm that messages have been successfully published.
    /// </summary>
    public bool PublisherConfirmationsEnabled { get; set; } = false;

    /// <summary>
    /// Indicates whether tracking of publisher confirmations is enabled for RabbitMQ channels.
    /// When enabled, the system tracks the acknowledgment status of published messages
    /// to verify their successful delivery. Defaults to false.
    /// </summary>
    public bool PublisherConfirmationTrackingEnabled { get; set; } = false;

    /// <summary>
    /// Specifies the concurrency level for dispatching consumer messages in RabbitMQ channels.
    /// Defaults to 1. Adjusting this value can control the number of messages dispatched concurrently
    /// to consumers, which can be useful for optimizing performance in high-throughput scenarios.
    /// </summary>
    public ushort? ConsumerDispatchConcurrency { get; set; } = 1;
}