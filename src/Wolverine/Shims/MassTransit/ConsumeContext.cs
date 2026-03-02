using Wolverine.Attributes;

namespace Wolverine.Shims.MassTransit;

/// <summary>
/// MassTransit-compatible consume context interface.
/// Provides access to the message being consumed and messaging operations.
/// </summary>
/// <typeparam name="T">The message type</typeparam>
[WolverineMessageWrapper]
public interface ConsumeContext<out T> where T : class
{
    /// <summary>
    /// The message being consumed.
    /// </summary>
    T Message { get; }

    /// <summary>
    /// The unique identifier of the message.
    /// Maps to <see cref="Envelope.Id"/>.
    /// </summary>
    Guid? MessageId { get; }

    /// <summary>
    /// The correlation identifier for tracking related messages.
    /// Maps to <see cref="IMessageContext.CorrelationId"/>.
    /// </summary>
    string? CorrelationId { get; }

    /// <summary>
    /// The conversation identifier for the logical workflow.
    /// Maps to <see cref="Envelope.ConversationId"/>.
    /// </summary>
    Guid? ConversationId { get; }

    /// <summary>
    /// The headers of the message being consumed.
    /// Maps to <see cref="Envelope.Headers"/>.
    /// </summary>
    IReadOnlyDictionary<string, string?> Headers { get; }

    /// <summary>
    /// Publishes an event message.
    /// Maps to <see cref="IMessageBus.PublishAsync{T}"/>.
    /// </summary>
    Task Publish<TMessage>(TMessage message) where TMessage : class;

    /// <summary>
    /// Sends a command message.
    /// Maps to <see cref="IMessageBus.SendAsync{T}"/>.
    /// </summary>
    Task Send<TMessage>(TMessage message) where TMessage : class;

    /// <summary>
    /// Sends a command message to a specific endpoint.
    /// Maps to <see cref="IMessageBus.EndpointFor(Uri)"/> then <see cref="IDestinationEndpoint.SendAsync{T}"/>.
    /// </summary>
    Task Send<TMessage>(TMessage message, Uri destinationAddress) where TMessage : class;

    /// <summary>
    /// Sends a response back to the request originator.
    /// Maps to <see cref="IMessageContext.RespondToSenderAsync"/>.
    /// </summary>
    Task RespondAsync<TMessage>(TMessage message) where TMessage : class;
}
