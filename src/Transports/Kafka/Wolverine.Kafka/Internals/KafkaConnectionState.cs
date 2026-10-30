using Dekaf.Diagnostics;
using Wolverine.Transports;

namespace Wolverine.Kafka.Internals;

/// <summary>
/// Maps the broker connection status a Dekaf client reports onto <see cref="TransportConnectionState"/> (GH-3454).
/// </summary>
internal static class KafkaConnectionState
{
    public static TransportConnectionState From(KafkaClientStatus status)
    {
        if (status.IsStopped)
        {
            return TransportConnectionState.Disconnected;
        }

        // A client only holds connections to the brokers it needs (partition leaders, the group coordinator), so
        // one live connection means it is working. With none, Dekaf keeps reconnecting on its own.
        foreach (var broker in status.Brokers)
        {
            if (broker.State != BrokerConnectionState.Disconnected)
            {
                return TransportConnectionState.Connected;
            }
        }

        return TransportConnectionState.Reconnecting;
    }
}
