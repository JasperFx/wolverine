using NATS.Client.Core;
using NATS.Client.JetStream;

namespace Wolverine.Nats.Internal;

public class NatsEnvelope : Envelope
{
    public NatsEnvelope(NatsMsg<byte[]>? coreMsg, INatsJSMsg<byte[]>? jetStreamMsg)
    {
        CoreMsg = coreMsg;
        JetStreamMsg = jetStreamMsg;
    }

    public NatsMsg<byte[]>? CoreMsg { get; }
    public INatsJSMsg<byte[]>? JetStreamMsg { get; }
}
