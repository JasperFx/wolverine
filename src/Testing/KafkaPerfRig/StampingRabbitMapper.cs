using System.Diagnostics;
using RabbitMQ.Client;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.RabbitMQ.Internal;
using Wolverine.Runtime;

namespace KafkaPerfRig;

/// <summary>
/// RabbitMQ twin of <see cref="StampingKafkaMapper"/>: the default mapper plus the monotonic
/// consume-callback timestamp (the rig's t2 stage) stamped as an incoming header.
/// </summary>
public class StampingRabbitMapper : RabbitMqEnvelopeMapper
{
    public StampingRabbitMapper(Endpoint endpoint, IWolverineRuntime runtime) : base(endpoint, runtime)
    {
    }

    protected override void writeIncomingHeaders(IReadOnlyBasicProperties incoming, Envelope envelope)
    {
        base.writeIncomingHeaders(incoming, envelope);
        envelope.Headers[StampingKafkaMapper.ConsumeTimestampHeader] = Stopwatch.GetTimestamp().ToString();
    }
}
