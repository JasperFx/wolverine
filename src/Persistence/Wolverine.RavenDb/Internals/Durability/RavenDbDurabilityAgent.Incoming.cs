using Microsoft.Extensions.Logging;
using Raven.Client.Documents;
using Wolverine.Logging;
using Wolverine.Transports;

namespace Wolverine.RavenDb.Internals.Durability;

public partial class RavenDbDurabilityAgent
{

    private async Task tryRecoverIncomingMessages()
    {
        try
        {
            using var session = _store.OpenAsyncSession();
            var listeners = await session.Query<IncomingMessage>()
                .Where(x => x.OwnerId == 0)
                .Select(x => x.ReceivedAt)
                .Distinct()
                .ToListAsync();

            foreach (var listener in listeners)
            {
                var circuit = _runtime.Endpoints.FindListenerCircuit(listener);
                if (circuit.Status != ListeningStatus.Accepting)
                {
                    continue;
                }

                // Harden around this!
                await recoverMessagesForListener(listener, circuit);
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error trying to recover messages from the durable inbox");
        }
    }

    private async Task recoverMessagesForListener(Uri listener, IListenerCircuit circuit)
    {
        try
        {
            var envelopes = await _parent.LoadPageOfGloballyOwnedIncomingAsync(listener, _settings.RecoveryBatchSize);
            await _parent.ReassignIncomingAsync(_settings.AssignedNodeNumber, envelopes);

            await circuit.EnqueueDirectlyAsync(envelopes);
            _logger.RecoveredIncoming(envelopes);

            _logger.LogInformation("Successfully recovered {Count} messages from the inbox for listener {Listener}",
                envelopes.Count, listener);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error trying to recover messages from the inbox for listener {Uri}", listener);
        }
    }
    
}