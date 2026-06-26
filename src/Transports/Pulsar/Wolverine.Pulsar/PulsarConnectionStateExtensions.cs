using DotPulsar;
using Wolverine.Transports;

namespace Wolverine.Pulsar;

internal static class PulsarConnectionStateExtensions
{
    // GH-3231: map DotPulsar's consumer/reader state onto Wolverine's transport-agnostic enum so a Pulsar listener's
    // connection health flows to EndpointHealthSnapshot. Anything other than an actively-usable state is reported as
    // Disconnected so an external monitor can see a listener that has silently stopped consuming (e.g. a Fenced or
    // Faulted consumer) even while its ListeningStatus still reads Accepting.
    public static TransportConnectionState ToTransportConnectionState(this ConsumerState state) => state switch
    {
        ConsumerState.Active => TransportConnectionState.Connected,
        ConsumerState.Inactive => TransportConnectionState.Connected,         // Failover standby — connected, just not the active consumer
        ConsumerState.ReachedEndOfTopic => TransportConnectionState.Connected,
        ConsumerState.Disconnected => TransportConnectionState.Disconnected,
        _ => TransportConnectionState.Disconnected                            // Closed, Fenced, Faulted, Unsubscribed
    };

    public static TransportConnectionState ToTransportConnectionState(this ReaderState state) => state switch
    {
        ReaderState.Connected => TransportConnectionState.Connected,
        ReaderState.ReachedEndOfTopic => TransportConnectionState.Connected,
        ReaderState.Disconnected => TransportConnectionState.Disconnected,
        _ => TransportConnectionState.Disconnected                            // Closed, Faulted
    };
}
