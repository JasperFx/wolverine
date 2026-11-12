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
        // one live or idle connection means it is working
        foreach (var broker in status.Brokers)
        {
            if (broker.State != BrokerConnectionState.Disconnected || isIdle(broker))
            {
                return TransportConnectionState.Connected;
            }
        }

        return TransportConnectionState.Reconnecting;
    }

    // Dekaf closes connections that sit idle (9 minutes by default), so a producer with nothing to send reports its
    // brokers as disconnected. With no connection left and no failure since the last successful request, the
    // broker was reachable when the client last needed it
    private static bool isIdle(BrokerConnectionStatus broker)
    {
        return broker.ConnectionCount == 0
               && broker.LastSuccessfulRequestAtUtc is { } succeeded
               && (broker.LastErrorAtUtc is not { } failed || failed < succeeded);
    }
}
