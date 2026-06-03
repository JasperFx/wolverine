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
    private readonly NatsConnection _connection;
    private readonly INatsJSContext _jetStreamContext;
    private readonly ILogger<NatsEndpoint> _logger;
    private readonly string _scheduleSubjectSuffix;

    public JetStreamPublisher(NatsConnection connection, 
        ILogger<NatsEndpoint> logger,
        string scheduleSubjectSuffix = ".scheduled")
    {
        _connection = connection;
        _logger = logger;
        // An empty suffix would make the schedule subject equal the target and re-trigger err 10190.
        _scheduleSubjectSuffix = string.IsNullOrWhiteSpace(scheduleSubjectSuffix) ? ".scheduled" : scheduleSubjectSuffix;
        _jetStreamContext = connection.CreateJetStreamContext();
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
