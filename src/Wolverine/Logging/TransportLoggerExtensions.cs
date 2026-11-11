using Microsoft.Extensions.Logging;
using Wolverine.Transports;

namespace Wolverine.Logging;

public static class TransportLoggerExtensions
{
    public const int OutgoingBatchSucceededEventId = 200;
    public const int OutgoingBatchFailedEventId = 201;
    public const int IncomingBatchReceivedEventId = 202;
    public const int CircuitBrokenEventId = 203;
    public const int CircuitBrokenResumedId = 204;
    public const int ScheduledJobsQueuedForExecutionEventId = 205;
    public const int RecoveredIncomingEventId = 206;
    public const int RecoveredOutgoingEventId = 207;
    public const int DiscardedExpiredEventId = 208;
    public const int DiscardedUnknownTransportEventId = 209;
    public const int ListeningStatusChangedEventId = 210;
    private static readonly Action<ILogger, Uri, Exception?> _circuitBroken;
    private static readonly Action<ILogger, Uri, Exception?> _circuitResumed;
    private static readonly Action<ILogger, string, Guid, Exception?> _discardedExpired;
    private static readonly Action<ILogger, string, Guid, Exception?> _discardedUnknownTransport;
    private static readonly Action<ILogger, int, Uri, Exception?> _incomingBatchReceived;
    private static readonly Action<ILogger, ListeningStatus, Exception?> _listeningStatusChanged;

    private static readonly Action<ILogger, Uri, Exception?> _outgoingBatchFailed;
    private static readonly Action<ILogger, int, Uri, Exception?> _outgoingBatchSucceeded;
    private static readonly Action<ILogger, int, Exception?> _recoveredIncoming;
    private static readonly Action<ILogger, int, Exception?> _recoveredOutgoing;
    private static readonly Action<ILogger, string, Guid, DateTimeOffset, Exception?> _scheduledJobsQueued;
    private static readonly Action<ILogger, Uri, Exception?> _incomingReceived;

    static TransportLoggerExtensions()
    {
        _outgoingBatchSucceeded = LoggerMessage.Define<int, Uri>(LogLevel.Debug, OutgoingBatchSucceededEventId,
            "Successfully sent {Count} messages to {Destination}");

        _outgoingBatchFailed = LoggerMessage.Define<Uri>(LogLevel.Error, OutgoingBatchFailedEventId,
            "Failed to send outgoing envelopes batch to {Destination}");

        _incomingBatchReceived = LoggerMessage.Define<int, Uri>(LogLevel.Debug, IncomingBatchReceivedEventId,
            "Received {Count} message(s) from {ReplyUri}");

        _incomingReceived = LoggerMessage.Define<Uri>(LogLevel.Debug, IncomingBatchReceivedEventId,
            "Received message from {ReplyUri}");

        _circuitBroken = LoggerMessage.Define<Uri>(LogLevel.Error, CircuitBrokenEventId,
            "Sending agent for {destination} is latched");

        _circuitResumed = LoggerMessage.Define<Uri>(LogLevel.Information, CircuitBrokenResumedId,
            "Sending agent for {destination} has resumed");

        _scheduledJobsQueued =
            LoggerMessage.Define<string, Guid, DateTimeOffset>(LogLevel.Information,
                ScheduledJobsQueuedForExecutionEventId,
                "Envelope {Name}#{Id} was scheduled locally for {date}");

        _recoveredIncoming = LoggerMessage.Define<int>(LogLevel.Information, RecoveredIncomingEventId,
            "Recovered {Count} incoming envelopes from storage");

        _recoveredOutgoing = LoggerMessage.Define<int>(LogLevel.Information, RecoveredOutgoingEventId,
            "Recovered {Count} outgoing envelopes from storage");

        _discardedExpired = LoggerMessage.Define<string, Guid>(LogLevel.Debug, DiscardedExpiredEventId,
            "Discarded expired envelope {Name}#{Id}");

        _discardedUnknownTransport =
            LoggerMessage.Define<string, Guid>(LogLevel.Information, DiscardedUnknownTransportEventId,
                "Discarded {Name}#{Id} with unknown transport");

        _listeningStatusChanged = LoggerMessage.Define<ListeningStatus>(LogLevel.Information,
            ListeningStatusChangedEventId, "ListeningStatus changed to {status}");
    }

    public static void OutgoingBatchSucceeded(this ILogger logger, OutgoingMessageBatch batch)
    {
        _outgoingBatchSucceeded(logger, batch.Messages.Count, batch.Destination, null);
    }

    public static void OutgoingBatchFailed(this ILogger logger, OutgoingMessageBatch batch, Exception? ex = null)
    {
        _outgoingBatchFailed(logger, batch.Destination, ex);
    }

    public static void IncomingBatchReceived(this ILogger logger, Uri address, IReadOnlyList<Envelope> envelopes)
    {
        _incomingBatchReceived(logger, envelopes.Count, address, null);
    }

    public static void IncomingReceived(this ILogger logger, Envelope envelope, Uri address)
    {
        _incomingReceived(logger, address, null);
    }

    public static void CircuitBroken(this ILogger logger, Uri destination)
    {
        _circuitBroken(logger, destination, null);
    }

    public static void CircuitResumed(this ILogger logger, Uri destination)
    {
        _circuitResumed(logger, destination, null);
    }

    public static void ScheduledJobsQueuedForExecution(this ILogger logger, IEnumerable<Envelope> envelopes)
    {
        foreach (var envelope in envelopes)
            _scheduledJobsQueued(logger, envelope.GetMessageTypeName(), envelope.Id, envelope.ScheduledTime ?? DateTimeOffset.UtcNow, null);
    }

    public static void RecoveredIncoming(this ILogger logger, IEnumerable<Envelope> envelopes)
    {
        var count = envelopes.Count();
        if (count == 0) return;
        _recoveredIncoming(logger, count, null);
    }

    public static void RecoveredOutgoing(this ILogger logger, IEnumerable<Envelope> envelopes)
    {
        var count = envelopes.Count();
        if (count == 0) return;
        _recoveredOutgoing(logger, count, null);
    }

    public static void DiscardedExpired(this ILogger logger, IEnumerable<Envelope> envelopes)
    {
        foreach (var envelope in envelopes) _discardedExpired(logger, envelope.GetMessageTypeName(), envelope.Id, null);
    }

    public static void DiscardedUnknownTransport(this ILogger logger, IEnumerable<Envelope> envelopes)
    {
        foreach (var envelope in envelopes) _discardedUnknownTransport(logger, envelope.GetMessageTypeName(), envelope.Id, null);
    }

    public static void ListeningStatusChange(this ILogger logger, ListeningStatus status)
    {
        _listeningStatusChanged(logger, status, null);
    }
}