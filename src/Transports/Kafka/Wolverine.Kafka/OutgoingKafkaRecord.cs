using Dekaf.Producer;
using Dekaf.Serialization;

namespace Wolverine.Kafka;

/// <summary>
/// Mutable outgoing Kafka record populated by <see cref="IKafkaEnvelopeMapper"/> implementations
/// before being handed to the Dekaf producer. A null <see cref="Value"/> produces a tombstone
/// record for log-compacted topics.
/// </summary>
public class OutgoingKafkaRecord
{
    public string? Key { get; set; }

    public byte[]? Value { get; set; }

    // Room for Wolverine's reserved headers, so the list does not regrow while the mapper writes them
    public Headers Headers { get; } = new(16);

    public DateTimeOffset? Timestamp { get; set; }

    public int? Partition { get; set; }

    internal ProducerMessage<string, byte[]> ToProducerMessage(string topic)
    {
        return new ProducerMessage<string, byte[]>
        {
            Topic = topic,
            Key = Key!,
            Value = Value!,
            Headers = Headers,
            Timestamp = Timestamp,
            Partition = Partition
        };
    }
}
