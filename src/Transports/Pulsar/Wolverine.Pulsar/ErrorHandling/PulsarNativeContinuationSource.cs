using Wolverine.ErrorHandling;
using Wolverine.Runtime;

namespace Wolverine.Pulsar.ErrorHandling;

public class PulsarNativeContinuationSource : IContinuationSource
{
    public string Description { get; }

    public IContinuation Build(Exception ex, Envelope envelope)
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