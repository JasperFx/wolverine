using Microsoft.Extensions.Logging;
using NATS.Client.Core;

namespace Wolverine.Nats.Internal;

/// <summary>
/// Core NATS publisher for sending messages
/// </summary>
internal class CoreNatsPublisher : INatsPublisher
{
    private readonly NatsConnection _connection;
    private readonly ILogger<NatsEndpoint> _logger;

    public CoreNatsPublisher(NatsConnection connection, ILogger<NatsEndpoint> logger)
    {
        _connection = connection;
        _logger = logger;
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
                "Message {MessageId} published via Core NATS to {Subject}",
                envelope.Id,
                subject
            );
        }
    }
}
