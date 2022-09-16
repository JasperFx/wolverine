using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Wolverine.Logging;
using Microsoft.Extensions.Logging;
using Wolverine.Runtime;
using Wolverine.Runtime.WorkerQueues;
using Wolverine.Transports;

namespace Wolverine.Persistence.Durability;

public class RecoverIncomingMessages : IMessagingAction
{
    private readonly ILogger _logger;
    private readonly IWolverineRuntime _runtime;
    private readonly AdvancedSettings _settings;
    private readonly ILocalQueue _locals;

    public RecoverIncomingMessages(ILocalQueue locals, AdvancedSettings settings,
        ILogger logger, IWolverineRuntime runtime)
    {
        _locals = locals;
        _settings = settings;
        _logger = logger;
        _runtime = runtime;
    }

    public async Task ExecuteAsync(IEnvelopePersistence storage, IDurabilityAgent agent)
    {
        // TODO -- enforce back pressure here on the retries listener!

        await storage.Session.BeginAsync();

        var incoming = await determineIncomingAsync(storage);

        if (incoming.Count > 0)
        {
            _logger.RecoveredIncoming(incoming);

            foreach (var envelope in incoming)
            {
                envelope.OwnerId = _settings.UniqueNodeId;
                _locals.Enqueue(envelope);
            }

            // TODO -- this should be smart enough later to check for back pressure before rescheduling
            if (incoming.Count == _settings.RecoveryBatchSize)
            {
                agent.RescheduleIncomingRecovery();
            }
        }
    }

    public string Description { get; } = "Recover persisted incoming messages";

    private async Task<IReadOnlyList<Envelope>> determineIncomingAsync(IEnvelopePersistence storage)
    {
        try
        {
            var gotLock = await storage.Session.TryGetGlobalLockAsync(TransportConstants.IncomingMessageLockId);

            if (!gotLock)
            {
                await storage.Session.RollbackAsync();
                return new List<Envelope>();
            }

            var incoming = await storage.LoadPageOfGloballyOwnedIncomingAsync();

            if (!incoming.Any())
            {
                await storage.Session.RollbackAsync();
                return incoming; // Okay to return the empty list here any way
            }

            // Got to filter out paused listeners here
            var latched = _runtime
                .ActiveListeners()
                .Where(x => x.Status == ListeningStatus.Stopped)
                .Select(x => x.Uri)
                .ToArray();

            if (latched.Any())
            {
                incoming = incoming.Where(e => !latched.Contains(e.Destination)).ToList();
            }

            await storage.ReassignIncomingAsync(_settings.UniqueNodeId, incoming);

            await storage.Session.CommitAsync();

            return incoming;
        }
        catch (Exception)
        {
            await storage.Session.RollbackAsync();
            throw;
        }
        finally
        {
            await storage.Session.ReleaseGlobalLockAsync(TransportConstants.IncomingMessageLockId);
        }

        return new List<Envelope>();
    }
}
