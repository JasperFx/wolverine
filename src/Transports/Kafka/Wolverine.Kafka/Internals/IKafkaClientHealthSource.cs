using Dekaf.Diagnostics;

namespace Wolverine.Kafka.Internals;

/// <summary>
/// A Dekaf client whose live status the Kafka health check reads instead of probing the broker.
/// </summary>
internal interface IKafkaClientHealthSource
{
    /// <summary>
    /// The client's current status, or null until it has connected
    /// </summary>
    KafkaClientStatus? Status { get; }

    /// <summary>
    /// The client could not make its first connection to the broker
    /// </summary>
    bool FailedToConnect { get; }
}
