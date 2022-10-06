using System;
using Microsoft.Extensions.Logging;
using Wolverine.Logging;
using Wolverine.Tracking;

namespace Wolverine.Runtime;

internal sealed partial class WolverineRuntime : IMessageLogger
{
    public const int SentEventId = 100;
    public const int ReceivedEventId = 101;
    public const int ExecutionStartedEventId = 102;
    public const int ExecutionFinishedEventId = 103;
    public const int MessageSucceededEventId = 104;
    public const int MessageFailedEventId = 105;
    public const int NoHandlerEventId = 106;
    public const int NoRoutesEventId = 107;
    public const int MovedToErrorQueueId = 108;
    public const int UndeliverableEventId = 108;
    private static readonly Action<ILogger, string, Guid, Exception> _executionFinished;
    private static readonly Action<ILogger, string, Guid, Exception> _executionStarted;

    private static readonly Action<ILogger, string, Guid, string, Exception> _messageFailed;
    private static readonly Action<ILogger, string, Guid, string, Exception> _messageSucceeded;
    private static readonly Action<ILogger, Envelope, Exception> _movedToErrorQueue;
    private static readonly Action<ILogger, string, Guid, string, Exception> _noHandler;
    private static readonly Action<ILogger, Envelope, Exception> _noRoutes;
    private static readonly Action<ILogger, string, Guid, string, string, Exception> _received;
    private static readonly Action<ILogger, string, Guid, string, Exception> _sent;
    private static readonly Action<ILogger, Envelope, Exception> _undeliverable;

    static WolverineRuntime()
    {
        _sent = LoggerMessage.Define<string, Guid, string>(LogLevel.Debug, SentEventId,
            "Enqueued for sending {Name}#{Id} to {Destination}");

        _received = LoggerMessage.Define<string, Guid, string, string>(LogLevel.Debug, ReceivedEventId,
            "Received {Name}#{Id} at {Destination} from {ReplyUri}");

        _executionStarted = LoggerMessage.Define<string, Guid>(LogLevel.Debug, ExecutionStartedEventId,
            "Started processing {Name}#{Id}");

        _executionFinished = LoggerMessage.Define<string, Guid>(LogLevel.Debug, ExecutionFinishedEventId,
            "Finished processing {Name}#{Id}");

        _messageSucceeded =
            LoggerMessage.Define<string, Guid, string>(LogLevel.Information, MessageSucceededEventId,
                "Successfully processed message {Name}#{envelope} from {ReplyUri}");

        _messageFailed = LoggerMessage.Define<string, Guid, string>(LogLevel.Error, MessageFailedEventId,
            "Failed to process message {Name}#{envelope} from {ReplyUri}");

        _noHandler = LoggerMessage.Define<string, Guid, string>(LogLevel.Information, NoHandlerEventId,
            "No known handler for {Name}#{Id} from {ReplyUri}");

        _noRoutes = LoggerMessage.Define<Envelope>(LogLevel.Information, NoRoutesEventId,
            "No routes can be determined for {envelope}");

        _movedToErrorQueue = LoggerMessage.Define<Envelope>(LogLevel.Error, MovedToErrorQueueId,
            "Envelope {envelope} was moved to the error queue");

        _undeliverable = LoggerMessage.Define<Envelope>(LogLevel.Information, UndeliverableEventId,
            "Discarding {envelope}");
    }

    public TrackedSession? ActiveSession { get; internal set; }

    public void Sent(Envelope envelope)
    {
        ActiveSession?.Record(EventType.Sent, envelope, _serviceName, _uniqueNodeId);
        _sent(Logger, envelope.GetMessageTypeName(), envelope.Id, envelope.Destination?.ToString(), null);
    }

    // TODO -- add a URI for the received point
    public void Received(Envelope envelope)
    {
        ActiveSession?.Record(EventType.Received, envelope, _serviceName, _uniqueNodeId);
        _received(Logger, envelope.GetMessageTypeName(), envelope.Id, envelope.Destination?.ToString(),
            envelope.ReplyUri?.ToString(), null);
    }

    public void ExecutionStarted(Envelope envelope)
    {
        ActiveSession?.Record(EventType.ExecutionStarted, envelope, _serviceName, _uniqueNodeId);
        _executionStarted(Logger, envelope.GetMessageTypeName(), envelope.Id, null);
    }

    public void ExecutionFinished(Envelope envelope)
    {
        ActiveSession?.Record(EventType.ExecutionFinished, envelope, _serviceName, _uniqueNodeId);
        _executionFinished(Logger, envelope.GetMessageTypeName(), envelope.Id, null);
    }

    public void MessageSucceeded(Envelope envelope)
    {
        ActiveSession?.Record(EventType.MessageSucceeded, envelope, _serviceName, _uniqueNodeId);
        // TODO -- bring back: _metrics.MessageExecuted(envelope);
        _messageSucceeded(Logger, envelope.GetMessageTypeName(), envelope.Id, envelope.ReplyUri?.ToString(), null);
    }

    public void MessageFailed(Envelope envelope, Exception ex)
    {
        ActiveSession?.Record(EventType.Sent, envelope, _serviceName, _uniqueNodeId, ex);
        // TODO -- bring back: _metrics.MessageExecuted(envelope);
        _messageFailed(Logger, envelope.GetMessageTypeName(), envelope.Id, envelope.ReplyUri?.ToString(), ex);
    }

    public void NoHandlerFor(Envelope envelope)
    {
        ActiveSession?.Record(EventType.NoHandlers, envelope, _serviceName, _uniqueNodeId);
        _noHandler(Logger, envelope.GetMessageTypeName(), envelope.Id, envelope.ReplyUri?.ToString(), null);
    }

    public void NoRoutesFor(Envelope envelope)
    {
        ActiveSession?.Record(EventType.NoRoutes, envelope, _serviceName, _uniqueNodeId);
        _noRoutes(Logger, envelope, null);
    }

    public void MovedToErrorQueue(Envelope envelope, Exception ex)
    {
        ActiveSession?.Record(EventType.MovedToErrorQueue, envelope, _serviceName, _uniqueNodeId);
        _movedToErrorQueue(Logger, envelope, ex);
    }

    public void DiscardedEnvelope(Envelope envelope)
    {
        _undeliverable(Logger, envelope, null);
    }

    public void LogException(Exception ex, object? correlationId = null,
        string message = "Exception detected:")
    {
        ActiveSession?.LogException(ex, _serviceName);
        // TODO -- bring back: _metrics.LogException(ex);
        Logger.LogError(ex, message);
    }
}
