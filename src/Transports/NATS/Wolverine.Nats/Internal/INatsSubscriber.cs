using Wolverine.Transports;

namespace Wolverine.Nats.Internal;

/// <summary>
/// Internal interface for NATS subscribers
/// </summary>
internal interface INatsSubscriber : IAsyncDisposable
{
    Task StartAsync(IListener listener, IReceiver receiver, CancellationToken cancellation);
    bool SupportsNativeDeadLetterQueue { get; }

    /// <summary>
    /// The current connection state of the underlying NATS connection, mapped onto Wolverine's transport-agnostic enum.
    /// </summary>
    TransportConnectionState ConnectionState { get; }
    
    /// <summary>
    /// Republish a message to the subject for requeue support in Core NATS
    /// </summary>
    Task RepublishAsync(NatsEnvelope envelope, CancellationToken cancellation);
}
