using Wolverine.Transports;

namespace Wolverine.Runtime;

public sealed partial class WolverineRuntime
{
    public async ValueTask EnqueueDirectlyAsync(IReadOnlyList<Envelope> envelopes)
    {
        var groups = envelopes.GroupBy(x => x.Destination ?? TransportConstants.LocalUri).ToArray();
        foreach (var group in groups)
        {
            var listener = Endpoints.FindListenerCircuit(group.Key);
            if (listener != null)
            {
                await listener.EnqueueDirectlyAsync(group);
            }
            else
            {
                // For send-only endpoints (e.g. Azure Service Bus topics),
                // there is no listener circuit. Send through the sending agent instead.
                var sender = Endpoints.GetOrBuildSendingAgent(group.Key);
                foreach (var envelope in group)
                {
                    await sender.EnqueueOutgoingAsync(envelope);
                }
            }
        }
    }
}