using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using Wolverine.Transports;

namespace Wolverine.Logging;

internal static class TransportLoggerExtensions
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
    private static readonly Action<ILogger, Uri, Exception> _circuitBroken;
    private static readonly Action<ILogger, Uri, Exception?> _circuitResumed;
    private static readonly Action<ILogger, Envelope, Exception?> _discardedExpired;
    private static readonly Action<ILogger, Envelope, Exception?> _discardedUnknownTransport;
    private static readonly Action<ILogger, int, Uri, Exception?> _incomingBatchReceived;
    private static readonly Action<ILogger, ListeningStatus, Exception?> _listeningStatusChanged;

    private static readonly Action<ILogger, Uri, Exception?> _outgoingBatchFailed;
    private static readonly Action<ILogger, int, Uri, Exception?> _outgoingBatchSucceeded;
    private static readonly Action<ILogger, int, Exception?> _recoveredIncoming;
    private static readonly Action<ILogger, int, Exception?> _recoveredOutgoing;
    private static readonly Action<ILogger, Envelope, DateTimeOffset, Exception?> _scheduledJobsQueued;
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
            LoggerMessage.Define<Envelope, DateTimeOffset>(LogLevel.Information,
                ScheduledJobsQueuedForExecutionEventId,
                "Envelope {envelope} was scheduled locally for {date}");

        _recoveredIncoming = LoggerMessage.Define<int>(LogLevel.Information, RecoveredIncomingEventId,
            "Recovered {Count} incoming envelopes from storage");

        _recoveredOutgoing = LoggerMessage.Define<int>(LogLevel.Information, RecoveredOutgoingEventId,
            "Recovered {Count} outgoing envelopes from storage");

        _discardedExpired = LoggerMessage.Define<Envelope>(LogLevel.Debug, DiscardedExpiredEventId,
            "Discarded expired envelope {envelope}");

        _discardedUnknownTransport =
            LoggerMessage.Define<Envelope>(LogLevel.Information, DiscardedUnknownTransportEventId,
                "Discarded {envelope} with unknown transport");

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

    public static void IncomingBatchReceived(this ILogger logger, IEnumerable<Envelope> envelopes)
    {
        _incomingBatchReceived(logger, envelopes.Count(), envelopes.FirstOrDefault()?.ReplyUri, null);
    }

    public static void IncomingReceived(this ILogger logger, Envelope envelope, Uri? address)
    {
        _incomingReceived(logger, envelope.ReplyUri, null);
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
            _scheduledJobsQueued(logger, envelope, envelope.ScheduledTime ?? DateTimeOffset.UtcNow, null);
    }

    public static void RecoveredIncoming(this ILogger logger, IEnumerable<Envelope> envelopes)
    {
        _recoveredIncoming(logger, envelopes.Count(), null);
    }

    public static void RecoveredOutgoing(this ILogger logger, IEnumerable<Envelope> envelopes)
    {
        _recoveredOutgoing(logger, envelopes.Count(), null);
    }

    public static void DiscardedExpired(this ILogger logger, IEnumerable<Envelope> envelopes)
    {
        foreach (var envelope in envelopes) _discardedExpired(logger, envelope, null);
    }

    public static void DiscardedUnknownTransport(this ILogger logger, IEnumerable<Envelope> envelopes)
    {
        foreach (var envelope in envelopes) _discardedUnknownTransport(logger, envelope, null);
    }

    public static void ListeningStatusChange(this ILogger logger, ListeningStatus status)
    {
        _listeningStatusChanged(logger, status, null);
    }
}
