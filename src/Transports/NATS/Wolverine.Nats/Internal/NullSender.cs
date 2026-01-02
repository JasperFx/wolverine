using Wolverine.Transports.Sending;

namespace Wolverine.Nats.Internal;

/// <summary>
/// Null object pattern for when no dead letter queue is configured
/// </summary>
internal class NullSender : ISender
{
    public bool SupportsNativeScheduledSend => false;
    public Uri Destination => new Uri("nats://null");

    public Task<bool> PingAsync() => Task.FromResult(true);

    public ValueTask SendAsync(Envelope envelope) => ValueTask.CompletedTask;
}
