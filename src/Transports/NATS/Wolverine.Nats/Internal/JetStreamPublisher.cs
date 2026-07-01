using Microsoft.Extensions.Logging;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Net;

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

    private readonly NatsConnection _connection;
    private readonly INatsJSContext _jetStreamContext;
    private readonly ILogger<NatsEndpoint> _logger;
    private readonly string _scheduleSubjectSuffix;
    private readonly Func<Envelope, string>? _msgIdSource;

    public JetStreamPublisher(NatsConnection connection,
        INatsJSContext jetStreamContext,
        ILogger<NatsEndpoint> logger,
        string scheduleSubjectSuffix = ".scheduled",
        Func<Envelope, string>? msgIdSource = null)
    {
        _connection = connection;
        _jetStreamContext = jetStreamContext;
        _logger = logger;
        _scheduleSubjectSuffix = scheduleSubjectSuffix;
        _msgIdSource = msgIdSource;
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

            if (envelope.ScheduledTime.HasValue)
            {
                // NATS rejects a scheduled publish whose subject equals Nats-Schedule-Target ("message
                // schedules target is invalid", err 10190). So the target stays the real destination
                // (where the consumer listens and the server materializes the message), and the control
                // message goes to a derived subject that must still be covered by the same stream.
                var scheduledTime = envelope.ScheduledTime.Value.ToUniversalTime();
                headers["Nats-Schedule"] = $"@at {scheduledTime:O}";
                headers["Nats-Schedule-Target"] = subject;
                publishSubject = subject + _scheduleSubjectSuffix;

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

            var ack = await _jetStreamContext.PublishAsync(
                publishSubject,
                data,
                opts: pubOpts,
                headers: headers,
                cancellationToken: cancellation
            );

            if (_logger.IsEnabled(LogLevel.Debug))
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
