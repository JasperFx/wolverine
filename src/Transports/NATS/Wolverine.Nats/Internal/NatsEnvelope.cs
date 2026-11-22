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

    /// <summary>
    /// GH-5773. Set once a core NATS requeue has counted its attempt, so the listener's RetryBlock retrying a
    /// failed republish of this same envelope does not count it again
    /// </summary>
    internal bool RequeueAttemptCounted { get; set; }
}
