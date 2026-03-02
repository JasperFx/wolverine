namespace Wolverine.Shims.NServiceBus;

/// <summary>
/// NServiceBus-compatible interface available during message handling.
/// Provides access to message metadata and the ability to send, publish, and reply.
/// Delegates to Wolverine's <see cref="IMessageContext"/>.
/// </summary>
public interface IMessageHandlerContext
{
    /// <summary>
    /// The unique identifier of the message currently being handled.
    /// Maps to <see cref="Envelope.Id"/>.
    /// </summary>
    string MessageId { get; }

    /// <summary>
    /// The address to which replies should be sent.
    /// Maps to <see cref="Envelope.ReplyUri"/>.
    /// </summary>
    string? ReplyToAddress { get; }

    /// <summary>
    /// The headers of the message currently being handled.
    /// Maps to <see cref="Envelope.Headers"/>.
    /// </summary>
    IReadOnlyDictionary<string, string?> MessageHeaders { get; }

    /// <summary>
    /// The correlation identifier for the current message workflow.
    /// Maps to <see cref="IMessageContext.CorrelationId"/>.
    /// </summary>
    string? CorrelationId { get; }

    /// <summary>
    /// Sends a command message.
    /// Maps to <see cref="IMessageBus.SendAsync{T}"/>.
    /// </summary>
    Task Send(object message, SendOptions? options = null);

    /// <summary>
    /// Publishes an event message.
    /// Maps to <see cref="IMessageBus.PublishAsync{T}"/>.
    /// </summary>
    Task Publish(object message, PublishOptions? options = null);

    /// <summary>
    /// Sends a reply back to the originator of the current message.
    /// Maps to <see cref="IMessageContext.RespondToSenderAsync"/>.
    /// </summary>
    Task Reply(object message, ReplyOptions? options = null);

    /// <summary>
    /// Sends the current message to a different endpoint for processing.
    /// Maps to sending the current message body to the specified destination.
    /// </summary>
    Task ForwardCurrentMessageTo(string destination);
}
