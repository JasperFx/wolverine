using NATS.Client.Core;

namespace Wolverine.Nats.Internal;

/// <summary>
/// Internal interface for NATS publishers
/// </summary>
internal interface INatsPublisher
{
    ValueTask<bool> PingAsync(CancellationToken cancellation);
    ValueTask PublishAsync(
        string subject,
        byte[] data,
        NatsHeaders headers,
        string? replyTo,
        Envelope envelope,
        CancellationToken cancellation
    );
}
