using Wolverine.ErrorHandling;
using Wolverine.Runtime;

namespace Wolverine.Pulsar.ErrorHandling;

public class PulsarNativeContinuationSource : IContinuationSource
{
    public string Description => "Pulsar native retry/DLQ handling";

#pragma warning disable CS8766 // Nullability of return type matches interface, null is valid for "not handled"
    public IContinuation? Build(Exception ex, Envelope envelope)
#pragma warning restore CS8766
    {
        // Only handle Pulsar envelopes/listeners
        if (envelope.Listener is PulsarListener)
        {
            return new PulsarNativeResiliencyContinuation(ex);
        }

        // Return null to let the next continuation source handle it
        return null;
    }
}