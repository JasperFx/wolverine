using Microsoft.Extensions.Logging;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using NATS.Net;
using Wolverine.Transports.Sending;

namespace Wolverine.Nats.Internal;

/// <summary>
/// JetStream publisher for sending messages with durability
/// </summary>
internal class JetStreamPublisher : INatsPublisher
{
    /// <summary>
    /// NATS JetStream deduplication header. When present, the server discards duplicate
    /// messages carrying the same value within the stream's configured duplicate window.
    /// </summary>
    internal const string NatsMsgIdHeader = "Nats-Msg-Id";

    /// <summary>
    /// Marks a native scheduling control message. The server removes it from the message it materializes at the
    /// due time, so only the control message itself carries it.
    /// </summary>
    internal const string NatsScheduleHeader = "Nats-Schedule";

    private readonly NatsConnection _connection;
    private readonly INatsJSContext _jetStreamContext;
    private readonly ILogger<NatsEndpoint> _logger;
    private readonly string _scheduleSubjectSuffix;
    private readonly Func<Envelope, string>? _msgIdSource;
    private readonly Func<Envelope, NatsJSPubOpts, NatsJSPubOpts>? _configurePublish;

    public JetStreamPublisher(NatsConnection connection,
        INatsJSContext jetStreamContext,
        ILogger<NatsEndpoint> logger,
        string scheduleSubjectSuffix = ".scheduled",
        Func<Envelope, string>? msgIdSource = null,
        Func<Envelope, NatsJSPubOpts, NatsJSPubOpts>? configurePublish = null)
    {
        _connection = connection;
        _jetStreamContext = jetStreamContext;
        _logger = logger;
        _scheduleSubjectSuffix = scheduleSubjectSuffix;
        _msgIdSource = msgIdSource;
        _configurePublish = configurePublish;
    }

    /// <summary>
    /// Resolve the JetStream <c>Nats-Msg-Id</c> deduplication key for an outgoing message:
    /// an explicit <c>Nats-Msg-Id</c> header wins (return null so we don't override it), then
    /// the configured <c>MsgIdSource</c>, else the Wolverine envelope Id.
    /// </summary>
    private NatsJSPubOpts? buildDedupOptions(Envelope envelope, NatsHeaders headers)
    {
        if (headers.ContainsKey(NatsMsgIdHeader))
        {
            return null;
        }

        var msgId = _msgIdSource?.Invoke(envelope) ?? envelope.Id.ToString();
        return string.IsNullOrEmpty(msgId) ? null : new NatsJSPubOpts { MsgId = msgId };
    }

    public async ValueTask<bool> PingAsync(CancellationToken cancellation)
    {
        try
        {
            var pingSubject = $"_INBOX.wolverine.ping.{Guid.NewGuid():N}";
            await _connection.PublishAsync(
                pingSubject,
                Array.Empty<byte>(),
                cancellationToken: cancellation
            );
            return _connection.ConnectionState == NatsConnectionState.Open;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to ping NATS endpoint");
            return false;
        }
    }

    public async ValueTask PublishAsync(
        string subject,
        byte[] data,
        NatsHeaders headers,
        string? replyTo,
        Envelope envelope,
        CancellationToken cancellation
    )
    {
        if (envelope.IsResponse)
        {
            await _connection.PublishAsync(
                subject,
                data,
                headers,
                replyTo,
                cancellationToken: cancellation
            );

            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(
                    "Reply message {MessageId} published via Core NATS to {Subject}",
                    envelope.Id,
                    subject
                );
            }
        }
        else
        {
            var publishSubject = subject;

            // Server-side dedup only applies to the direct publish path; the native scheduling
            // control message is materialized server-side and is not deduplicated by this key.
            var pubOpts = envelope.ScheduledTime.HasValue
                ? null
                : buildDedupOptions(envelope, headers);

            // GH-4860. The application's last word: it sees the options as built (Nats-Msg-Id resolved, or
            // none for a scheduled publish) and the publish uses what it hands back
            if (_configurePublish != null)
            {
                pubOpts = _configurePublish(envelope, pubOpts ?? new NatsJSPubOpts());
            }

            if (envelope.ScheduledTime.HasValue)
            {
                // NATS rejects a scheduled publish whose subject equals Nats-Schedule-Target ("message
                // schedules target is invalid", err 10190). So the target stays the real destination
                // (where the consumer listens and the server materializes the message), and the control
                // message goes to a derived subject that must still be covered by the same stream. NATS keeps
                // one schedule per subject and replaces it with the next control message on that subject, so
                // the envelope id makes the subject unique to this schedule: without it, the second message
                // scheduled for an endpoint silently cancelled the first.
                var scheduledTime = envelope.ScheduledTime.Value.ToUniversalTime();
                headers[NatsScheduleHeader] = $"@at {scheduledTime:O}";
                headers["Nats-Schedule-Target"] = subject;
                publishSubject = $"{subject}{_scheduleSubjectSuffix}.{envelope.Id:N}";

                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug(
                        "Scheduling message {MessageId} for delivery at {ScheduledTime} to {Target} via schedule subject {ScheduleSubject}",
                        envelope.Id,
                        scheduledTime,
                        subject,
                        publishSubject
                    );
                }
            }

            // A deadline of its own: with every server gone, NATS.Net keeps waiting for the PubAck far beyond its
            // request timeout -- minutes -- unless the token ends the wait, and the host's lifetime token never
            // does. The sending agent (or a dead-letter forward) would hang for as long as the outage lasts.
            var timeout = _connection.Opts.CommandTimeout;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            deadline.CancelAfter(timeout);

            PubAckResponse ack;
            try
            {
                ack = await _jetStreamContext.PublishAsync(
                    publishSubject,
                    data,
                    opts: pubOpts,
                    headers: headers,
                    cancellationToken: deadline.Token
                );
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"JetStream did not acknowledge the publish of message {envelope.Id} to {publishSubject} within {timeout}");
            }

            // NATS.Net hands a publish the server refused -- a full DiscardNew stream, a failed
            // Nats-Expected-* check, an oversized message -- back as a PubAckResponse carrying Error
            // instead of throwing. Unless that becomes an exception the sending agent reports the
            // envelope as sent, and a durable outbox deletes a message the stream never stored.
            // EnsureSuccess() is deliberately not used: it also throws for a duplicate Nats-Msg-Id,
            // and a duplicate means the stream already holds the message, so it stays a success.
            if (ack.Error is { } error)
            {
                var apiException = new NatsJSApiException(error);

                // GH-4955. The server answered the publish and said no, and no retry can change the answer,
                // so the sending agent is told not to try. Anything else -- no JetStream answering, a store
                // that is temporarily unable to write -- stays an ordinary failure that retries.
                if (IsRejection(error))
                {
                    throw new SendRejectedException(
                        $"JetStream refused the publish to '{publishSubject}' for message {envelope.Id}: {error.Description} (error {error.ErrCode})",
                        apiException);
                }

                throw apiException;
            }

            if (_logger.IsEnabled(LogLevel.Debug))
            {
                if (ack.Duplicate)
                {
                    _logger.LogDebug(
                        "Message {MessageId} was a JetStream duplicate of sequence {Sequence} in stream {Stream}",
                        envelope.Id,
                        ack.Seq,
                        ack.Stream
                    );
                }
                else
                {
                    _logger.LogDebug(
                        "Message {MessageId} published to JetStream with sequence {Sequence}",
                        envelope.Id,
                        ack.Seq
                    );
                }
            }
        }
    }

    // Stream limit refusals arrive as the generic "store failed" error (503, err_code 10077) with the limit in
    // the description, the way nats-server words them
    private static readonly string[] StreamLimitRefusals =
    [
        "maximum messages exceeded",
        "maximum bytes exceeded",
        "maximum messages per subject exceeded"
    ];

    /// <summary>
    /// GH-4955. Whether a JetStream publish error is the server REFUSING the message rather than failing to take
    /// it: every 400 (a failed Nats-Expected-* check, a message over the size limit), and a stream that is full
    /// under DiscardPolicy.New. That last one comes back as the generic store failure, 503 / err_code 10077,
    /// which also covers storage trouble that can clear up, so only the stream-limit descriptions count
    /// </summary>
    internal static bool IsRejection(ApiError error)
    {
        if (error.Code == 400)
        {
            return true;
        }

        return error.ErrCode == 10077 && error.Description is { } description &&
               StreamLimitRefusals.Any(x => description.Contains(x, StringComparison.OrdinalIgnoreCase));
    }
}
