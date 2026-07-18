using Confluent.Kafka;
using Microsoft.Extensions.Logging;

namespace Wolverine.Kafka.Internals;

/// <summary>
/// GH-3473: when the KIP-848 next-generation consumer rebalance protocol is enabled
/// (<c>group.protocol=consumer</c>), librdkafka *rejects* the classic-protocol client-side settings at
/// consumer creation time ("`partition.assignment.strategy` is not supported for `group.protocol=consumer`",
/// same for <c>session.timeout.ms</c>, <c>heartbeat.interval.ms</c>, and <c>group.protocol.type</c> — the
/// first is replaced by the broker-side <c>group.remote.assignor</c>, the timings are defined broker side).
/// Rather than letting every listener blow up mid-startup, Wolverine clears the conflicting settings during
/// transport bootstrap and logs a warning per cleared setting. Static membership (<c>group.instance.id</c>)
/// IS supported under KIP-848 and is deliberately left alone.
/// </summary>
internal static class KafkaGroupProtocolGuard
{
    public static void Sanitize(ConsumerConfig config, string scope, ILogger logger)
    {
        if (config.GroupProtocol != GroupProtocol.Consumer)
        {
            return;
        }

        if (config.PartitionAssignmentStrategy != null)
        {
            // Assigning null removes the key from the underlying config dictionary
            config.PartitionAssignmentStrategy = null;
            warn(logger, scope, "partition.assignment.strategy",
                "partition assignment is broker-driven under KIP-848 (see the broker-side group.remote.assignor setting), and cooperative incremental rebalancing is inherent to the protocol");
        }

        if (config.SessionTimeoutMs != null)
        {
            config.SessionTimeoutMs = null;
            warn(logger, scope, "session.timeout.ms",
                "the session timeout is defined broker side under KIP-848 (group.consumer.session.timeout.ms)");
        }

        if (config.HeartbeatIntervalMs != null)
        {
            config.HeartbeatIntervalMs = null;
            warn(logger, scope, "heartbeat.interval.ms",
                "the heartbeat interval is defined broker side under KIP-848 (group.consumer.heartbeat.interval.ms)");
        }

        if (config.GroupProtocolType != null)
        {
            config.GroupProtocolType = null;
            warn(logger, scope, "group.protocol.type",
                "the classic-protocol type name does not apply under KIP-848");
        }
    }

    private static void warn(ILogger logger, string scope, string setting, string reason)
    {
        logger.LogWarning(
            "Kafka consumer configuration ({Scope}) sets '{Setting}', which librdkafka rejects when the KIP-848 next-generation rebalance protocol (group.protocol=consumer) is enabled. Wolverine cleared the setting so the consumer can start: {Reason}.",
            scope, setting, reason);
    }
}
