using Dekaf.Admin;

namespace Wolverine.Kafka;

/// <summary>
/// Wolverine-owned, mutable specification for a Kafka topic created by AutoProvision. -1 for
/// <see cref="NumPartitions"/> or <see cref="ReplicationFactor"/> defers to the broker's defaults.
/// </summary>
public class KafkaTopicSpecification
{
    public string Name { get; set; } = string.Empty;

    public int NumPartitions { get; set; } = -1;

    public short ReplicationFactor { get; set; } = -1;

    /// <summary>
    /// Topic-level config entries, e.g. "cleanup.policy" = "compact"
    /// </summary>
    public Dictionary<string, string> Configs { get; } = new();

    /// <summary>
    /// Convert to the Dekaf admin client's topic-creation shape, e.g. inside a custom
    /// TopicCreation override
    /// </summary>
    public NewTopic ToNewTopic()
    {
        return new NewTopic
        {
            Name = Name,
            NumPartitions = NumPartitions,
            ReplicationFactor = ReplicationFactor,
            Configs = Configs
        };
    }
}
