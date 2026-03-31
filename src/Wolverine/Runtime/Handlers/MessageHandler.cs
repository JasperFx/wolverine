using System.Collections.Concurrent;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.Logging;
using Wolverine.Runtime.Agents;

namespace Wolverine.Runtime.Handlers;

#region sample_MessageHandler

public interface IMessageHandler
{
    Type MessageType { get; }

    LogLevel SuccessLogLevel { get; }

    LogLevel ProcessingLogLevel { get; }

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
    // Thread-safe set of known causation pairs: "IncomingType->OutgoingType"
    // Static per concrete handler type via the dictionary keyed by handler type
    private static readonly ConcurrentDictionary<string, byte> _knownCausation = new();

    public HandlerChain? Chain { get; set; }

    public abstract Task HandleAsync(MessageContext context, CancellationToken cancellation);

    public virtual Type MessageType => Chain!.MessageType;

    public LogLevel SuccessLogLevel => Chain!.SuccessLogLevel;
    public LogLevel ProcessingLogLevel => Chain!.ProcessingLogLevel;

    public bool TelemetryEnabled => Chain!.TelemetryEnabled;

    /// <summary>
    /// Records cause-and-effect relationships between the incoming message type
    /// and any outgoing messages produced during handling. Latched: each unique
    /// (incoming, outgoing) pair is only reported once to the observer.
    /// </summary>
    public void RecordCauseAndEffect(MessageContext context, IWolverineObserver observer)
    {
        if (!context.Runtime.Options.EnableMessageCausationTracking) return;

        var incomingType = MessageType.FullName ?? MessageType.Name;
        var handlerType = GetType().FullName ?? GetType().Name;
        var endpointUri = Chain?.Endpoints?.FirstOrDefault()?.Uri?.ToString();

        foreach (var envelope in context.Outstanding)
        {
            var outgoingType = envelope.Message?.GetType().FullName;
            if (string.IsNullOrEmpty(outgoingType)) continue;

            var key = $"{incomingType}->{outgoingType}@{handlerType}";

            // Latch: only report each unique causation pair once
            if (!_knownCausation.TryAdd(key, 0)) continue;

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

    protected abstract Task HandleAsync(T message, MessageContext context, CancellationToken cancellation);

    public override Type MessageType => typeof(T);
}