using NATS.Client.Core;
using Wolverine.Transports;

namespace Wolverine.Nats.Internal;

internal static class NatsConnectionStateExtensions
{
    // GH-3231: map the NATS.Net client's native connection state onto Wolverine's transport-agnostic enum so the
    // listener's connection health flows to EndpointHealthSnapshot.
    public static TransportConnectionState ToTransportConnectionState(this NatsConnectionState state) => state switch
    {
        NatsConnectionState.Open => TransportConnectionState.Connected,
        NatsConnectionState.Connecting => TransportConnectionState.Reconnecting,
        NatsConnectionState.Reconnecting => TransportConnectionState.Reconnecting,
        NatsConnectionState.Closed => TransportConnectionState.Disconnected,
        _ => TransportConnectionState.Unknown
    };
}
