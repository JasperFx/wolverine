using Wolverine.Transports;

namespace Wolverine.Nats.Internal;

/// <summary>
/// Internal interface for NATS subscribers
/// </summary>
internal interface INatsSubscriber : IAsyncDisposable
{
    Task StartAsync(IListener listener, IReceiver receiver, CancellationToken cancellation);
    bool SupportsNativeDeadLetterQueue { get; }
}
