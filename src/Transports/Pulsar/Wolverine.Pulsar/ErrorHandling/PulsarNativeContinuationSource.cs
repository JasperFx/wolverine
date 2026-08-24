using Wolverine.ErrorHandling;
using Wolverine.Runtime;

namespace Wolverine.Pulsar.ErrorHandling;

public class PulsarNativeContinuationSource : IContinuationSource
{
    public string Description => "Pulsar native retry/DLQ handling";

    public IContinuation? Build(Exception ex, Envelope envelope)
    {
        // GH-4079. Failure rules live on the handler graph rather than on an endpoint, so PulsarNativeResiliencyPolicy
        // has no choice but to register this one *globally* -- ahead of every opts.Policies.OnException<T>()
        // rule the user adds after UsePulsar(). That makes declining mandatory: this source may only claim a
        // failure it can genuinely act on, and everything else has to fall through to the user's own rules.
        //
        // Two things disqualify a failure:
        //   1. It did not arrive on a Pulsar listener at all -- any other transport, a local queue, or a
        //      hot-tail PulsarReaderListener, which is not a PulsarListener and has no cursor to move.
        //   2. It arrived on a Pulsar listener with no native resiliency configured, where
        //      PulsarNativeResiliencyContinuation would do literally nothing and the message would vanish.
        if (envelope.Listener is PulsarListener { HasNativeResiliency: true })
        {
            return new PulsarNativeResiliencyContinuation(ex);
        }

        return null;
    }
}
