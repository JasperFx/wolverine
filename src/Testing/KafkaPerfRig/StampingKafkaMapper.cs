using System.Diagnostics;
using Confluent.Kafka;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.Kafka.Internals;

namespace KafkaPerfRig;

/// <summary>
/// The default Kafka mapper plus one extra incoming header: the monotonic timestamp at
/// envelope-mapping time, which happens synchronously on the consume loop right after
/// IConsumer.Consume returns — i.e. the rig's t2 "consume return" stage.
/// </summary>
public class StampingKafkaMapper : KafkaEnvelopeMapper
{
    public const string ConsumeTimestampHeader = "rig-t2";

    public StampingKafkaMapper(Endpoint endpoint) : base(endpoint)
    {
    }

    protected override void writeIncomingHeaders(Message<string, byte[]> incoming, Envelope envelope)
    {
        base.writeIncomingHeaders(incoming, envelope);
        envelope.Headers[ConsumeTimestampHeader] = Stopwatch.GetTimestamp().ToString();
    }
}
