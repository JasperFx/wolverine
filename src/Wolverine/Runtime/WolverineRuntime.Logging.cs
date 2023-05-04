using System;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Wolverine.Logging;
using Wolverine.Tracking;

namespace Wolverine.Runtime;

public sealed partial class WolverineRuntime : IMessageLogger
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
    private static readonly Action<ILogger, string, string, Guid, Exception?> _executionFinished;
    private static readonly Action<ILogger, string, string, Guid, Exception?> _executionStarted;

    private static readonly Action<ILogger, string, Guid, string, Exception> _messageFailed;
    private static readonly Action<ILogger, string, Guid, string, Exception?> _messageSucceeded;
    private static readonly Action<ILogger, Envelope, Exception?> _movedToErrorQueue;
    private static readonly Action<ILogger, string?, string, Guid, string, Exception?> _noHandler;
    private static readonly Action<ILogger, Envelope, Exception?> _noRoutes;
    private static readonly Action<ILogger, string, string, Guid, string, string, Exception?> _received;
    private static readonly Action<ILogger, string, string, Guid, string, Exception?> _sent;
    private static readonly Action<ILogger, Envelope, Exception?> _undeliverable;
    private readonly Counter<int> _deadLetterQueueCounter;
    private readonly Histogram<double> _effectiveTime;
    private readonly Histogram<long> _executionCounter;
    private readonly Counter<int> _sentCounter;
    private readonly Counter<int> _successCounter;
    private readonly Counter<int> _failureCounter;

    static WolverineRuntime()
    {
        _sent = LoggerMessage.Define<string, string, Guid, string>(LogLevel.Debug, SentEventId,
            "{CorrelationId}: Enqueued for sending {Name}#{Id} to {Destination}");

        _received = LoggerMessage.Define<string, string, Guid, string, string>(LogLevel.Debug, ReceivedEventId,
            "{CorrelationId}: Received {Name}#{Id} at {Destination} from {ReplyUri}");

        _executionStarted = LoggerMessage.Define<string, string, Guid>(LogLevel.Debug, ExecutionStartedEventId,
            "{CorrelationId}: Started processing {Name}#{Id}");

        _executionFinished = LoggerMessage.Define<string, string, Guid>(LogLevel.Debug, ExecutionFinishedEventId,
            "{CorrelationId}: Finished processing {Name}#{Id}");

        _messageSucceeded =
            LoggerMessage.Define<string, Guid, string>(LogLevel.Information, MessageSucceededEventId,
                "Successfully processed message {Name}#{envelope} from {ReplyUri}");

        _messageFailed = LoggerMessage.Define<string, Guid, string>(LogLevel.Error, MessageFailedEventId,
            "Failed to process message {Name}#{envelope} from {ReplyUri}");

        _noHandler = LoggerMessage.Define<string?, string, Guid, string>(LogLevel.Information, NoHandlerEventId,
            "{CorrelationId}: No known handler for {Name}#{Id} from {ReplyUri}");

        _noRoutes = LoggerMessage.Define<Envelope>(LogLevel.Information, NoRoutesEventId,
            "No routes can be determined for {envelope}");

        _movedToErrorQueue = LoggerMessage.Define<Envelope>(LogLevel.Error, MovedToErrorQueueId,
            "Envelope {envelope} was moved to the error queue");

        _undeliverable = LoggerMessage.Define<Envelope>(LogLevel.Information, UndeliverableEventId,
            "Discarding {envelope}");
    }

    internal TrackedSession? ActiveSession { get; set; }

    public void Sent(Envelope envelope)
    {
        _sentCounter.Add(1, envelope.ToMetricsHeaders());
        ActiveSession?.Record(MessageEventType.Sent, envelope, _serviceName, _uniqueNodeId);
        _sent(Logger, envelope.CorrelationId, envelope.GetMessageTypeName(), envelope.Id, envelope.Destination?.ToString() ?? string.Empty,
            null);
    }

    public void Received(Envelope envelope)
    {
        ActiveSession?.Record(MessageEventType.Received, envelope, _serviceName, _uniqueNodeId);
        _received(Logger, envelope.CorrelationId, envelope.GetMessageTypeName(), envelope.Id, envelope.Destination?.ToString() ?? string.Empty,
            envelope.ReplyUri?.ToString() ?? string.Empty, null);
    }

    public void ExecutionStarted(Envelope envelope)
    {
        envelope.StartTiming();
        ActiveSession?.Record(MessageEventType.ExecutionStarted, envelope, _serviceName, _uniqueNodeId);
        _executionStarted(Logger, envelope.CorrelationId, envelope.GetMessageTypeName(), envelope.Id, null);
    }

    public void ExecutionFinished(Envelope envelope)
    {
        var time = envelope.StopTiming();
        if (time > 0)
        {
            _executionCounter.Record(time, envelope.ToMetricsHeaders());
        }

        ActiveSession?.Record(MessageEventType.ExecutionFinished, envelope, _serviceName, _uniqueNodeId);
        _executionFinished(Logger, envelope.CorrelationId, envelope.GetMessageTypeName(), envelope.Id, null);
    }

    public void ExecutionFinished(Envelope envelope, Exception exception)
    {
        ExecutionFinished(envelope);
        var tags = envelope.ToMetricsHeaders().Append(new (MetricsConstants.ExceptionType, exception.GetType().Name)).ToArray();
        _failureCounter.Add(1, tags);
    }

    public void MessageSucceeded(Envelope envelope)
    {
        var time = DateTimeOffset.UtcNow.Subtract(envelope.SentAt.ToUniversalTime()).TotalMilliseconds;
        _effectiveTime.Record(time, envelope.ToMetricsHeaders());

        _successCounter.Add(1, envelope.ToMetricsHeaders());

        ActiveSession?.Record(MessageEventType.MessageSucceeded, envelope, _serviceName, _uniqueNodeId);
        _messageSucceeded(Logger, envelope.GetMessageTypeName(), envelope.Id, envelope.Destination!.ToString(), null);
    }

    public void MessageFailed(Envelope envelope, Exception ex)
    {
        var time = DateTimeOffset.UtcNow.Subtract(envelope.SentAt.ToUniversalTime()).TotalMilliseconds;
        _effectiveTime.Record(time, envelope.ToMetricsHeaders());

        _deadLetterQueueCounter.Add(1, envelope.ToMetricsHeaders());

        ActiveSession?.Record(MessageEventType.Sent, envelope, _serviceName, _uniqueNodeId, ex);
        _messageFailed(Logger, envelope.GetMessageTypeName(), envelope.Id, envelope.Destination!.ToString(), ex);
    }

    public void NoHandlerFor(Envelope envelope)
    {
        ActiveSession?.Record(MessageEventType.NoHandlers, envelope, _serviceName, _uniqueNodeId);
        _noHandler(Logger, envelope.CorrelationId, envelope.GetMessageTypeName(), envelope.Id, envelope.ReplyUri?.ToString() ?? string.Empty,
            null);
    }

    public void NoRoutesFor(Envelope envelope)
    {
        ActiveSession?.Record(MessageEventType.NoRoutes, envelope, _serviceName, _uniqueNodeId);
        _noRoutes(Logger, envelope, null);
    }

    public void MovedToErrorQueue(Envelope envelope, Exception ex)
    {
        ActiveSession?.Record(MessageEventType.MovedToErrorQueue, envelope, _serviceName, _uniqueNodeId);
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
        Logger.LogError(ex, message);
    }
}