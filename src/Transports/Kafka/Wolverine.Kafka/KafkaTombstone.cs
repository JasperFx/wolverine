namespace Wolverine.Kafka;

/// <summary>
/// Sends a Kafka tombstone message to the specified topic key.
/// A tombstone is a message with a non-null key and a null value, used to
/// delete a key from a log-compacted Kafka topic.
/// </summary>
/// <param name="Key">The Kafka record key to tombstone.</param>
public record KafkaTombstone(string Key);
