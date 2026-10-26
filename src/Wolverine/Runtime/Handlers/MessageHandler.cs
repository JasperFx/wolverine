using JasperFx.Core.Reflection;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration.Capabilities;
using Wolverine.Runtime.Agents;

namespace Wolverine.Runtime.Handlers;

#region sample_messagehandler
public interface IMessageHandler
{
    Type MessageType { get; }

    LogLevel SuccessLogLevel { get; }

    LogLevel ProcessingLogLevel { get; }

    /// <summary>
    /// GH-5043. The level Wolverine logs a failed attempt to handle this message at. Error by default
    /// </summary>
    LogLevel FailureLogLevel => LogLevel.Error;

    /// <summary>
    /// Is OpenTelemetry logging enabled for invoking this message type?
    /// </summary>
    bool TelemetryEnabled { get; }

    Task HandleAsync(MessageContext context, CancellationToken cancellation);

    /// <summary>
    /// Records cause-and-effect relationships between incoming and outgoing messages.
    /// Called after handler execution but before flushing outgoing messages.
    /// Default is a no-op; MessageHandler provides the real implementation.
    /// </summary>
    void RecordCauseAndEffect(MessageContext context, IWolverineObserver observer)
    {
        // No-op by default
    }
}

public abstract class MessageHandler : IMessageHandler
{
    public HandlerChain? Chain { get; set; }

    // Lets HandlerGraph run MessageHandler<T>.ConfigureChain for a chain it assigns itself, without knowing T
    internal virtual void ApplyChainConfiguration(HandlerChain chain)
    {
    }

    public abstract Task HandleAsync(MessageContext context, CancellationToken cancellation);

    public virtual Type MessageType => Chain!.MessageType;

    public LogLevel SuccessLogLevel => Chain!.SuccessLogLevel;
    public LogLevel ProcessingLogLevel => Chain!.ProcessingLogLevel;
    public LogLevel FailureLogLevel => Chain?.FailureLogLevel ?? LogLevel.Error;

    public bool TelemetryEnabled => Chain!.TelemetryEnabled;

    /// <summary>
    /// Records cause-and-effect relationships between the incoming message type
    /// and any outgoing messages produced during handling. Latched: each unique
    /// (incoming, outgoing) pair is only reported once to the observer.
    ///
    /// Gating is owned by codegen: <c>RecordMessageCausationFrame</c> is the only
    /// caller in production hot paths and is emitted into the generated handler
    /// only when <c>WolverineOptions.Tracking.EnableMessageCausationTracking</c>
    /// is set at codegen time. No runtime <c>if/then</c> guard here — when the
    /// flag is off, this method is never called from generated handlers.
    /// </summary>
    /// <remarks>
    /// Retained for handler code generated before GH-5224, which calls this two-argument overload. It
    /// reports the generated <see cref="MessageHandler"/> subclass as the handler type. Code generated
    /// now calls <see cref="RecordCauseAndEffect(MessageContext, IWolverineObserver, string)"/> with the
    /// user's handler type name instead.
    /// </remarks>
    public void RecordCauseAndEffect(MessageContext context, IWolverineObserver observer)
    {
        RecordCauseAndEffect(context, observer, GetType().FullName ?? GetType().Name);
    }

    /// <summary>
    /// Records cause-and-effect relationships between the incoming message type
    /// and any outgoing messages produced during handling, attributing them to
    /// <paramref name="handlerType"/>. Latched: each unique (incoming, outgoing,
    /// handler) triple is only reported once to the observer.
    /// </summary>
    /// <param name="context">The current message context</param>
    /// <param name="observer">The runtime's observer</param>
    /// <param name="handlerType">
    /// The user handler type name(s), resolved at codegen time from the chain's handler calls. This is
    /// the same value as the <c>message.handler</c> span tag; several handler types are joined with
    /// ", ". See GH-5224.
    /// </param>
    public void RecordCauseAndEffect(MessageContext context, IWolverineObserver observer, string handlerType)
    {
        // Skip the entire causation report when the incoming message itself is a
        // framework-internal type (IAgentCommand, INotToBeRouted, IInternalMessage, etc.).
        // See GH-2520.
        if (MessageType.IsSystemMessageType()) return;

        var incomingType = MessageType.FullName ?? MessageType.Name;
        var endpointUri = Chain?.Endpoints?.FirstOrDefault()?.Uri?.ToString();

        foreach (var envelope in context.Outstanding)
        {
            var outgoingMessage = envelope.Message;
            if (outgoingMessage is null) continue;

            // Per-instance check uses fast pattern match over runtime type-tests for
            // the marker interfaces; falls through to the helper for assembly attrs.
            var outgoingMessageType = outgoingMessage.GetType();
            if (outgoingMessageType.IsSystemMessageType()) continue;

            var outgoingType = outgoingMessageType.FullName;
            if (string.IsNullOrEmpty(outgoingType)) continue;

            // Latch: only report each unique causation pair once. The store is shared with
            // EndpointCausation so an edge is reported once no matter which path first sees it.
            if (!CausationLatch.ShouldReport(incomingType, outgoingType!, handlerType)) continue;

            observer.MessageCausedBy(incomingType, outgoingType, handlerType, endpointUri);
        }
    }
}

#endregion

public abstract class MessageHandler<T> : MessageHandler
{
    public sealed override Task HandleAsync(MessageContext context, CancellationToken cancellation)
    {
        if (context.Envelope!.Message is T message)
        {
            return HandleAsync(message, context, cancellation);
        }

        throw new ArgumentOutOfRangeException(nameof(context),
            $"Wrong message type {context.Envelope!.Message!.GetType().FullNameInCode()}, expected {typeof(T).FullNameInCode()}");
    }

    /// <summary>
    /// Template method hook to optionally configure error handling policies for this message
    /// handler
    /// </summary>
    /// <param name="chain"></param>
    public virtual void ConfigureChain(HandlerChain chain)
    {
        // Nothing
    }

    internal sealed override void ApplyChainConfiguration(HandlerChain chain)
    {
        ConfigureChain(chain);
    }

    protected abstract Task HandleAsync(T message, MessageContext context, CancellationToken cancellation);

    public override Type MessageType => typeof(T);
}