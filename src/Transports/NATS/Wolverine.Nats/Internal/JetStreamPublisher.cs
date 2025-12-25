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

    public JetStreamPublisher(NatsConnection connection, ILogger<NatsEndpoint> logger)
    {
        _connection = connection;
        _logger = logger;
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
            var ack = await _jetStreamContext.PublishAsync(
                subject,
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
