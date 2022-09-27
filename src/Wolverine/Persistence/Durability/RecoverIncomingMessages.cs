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
        // We don't need this to be transactional
        var gotLock = await storage.Session.TryGetGlobalLockAsync(TransportConstants.IncomingMessageLockId);
        if (!gotLock)
        {
            return;
        }

        bool rescheduleImmediately = false;
        
        try
        {
            var counts = await storage.LoadAtLargeIncomingCountsAsync();
            foreach (var count in counts)
            {
                rescheduleImmediately = rescheduleImmediately || await TryRecoverIncomingMessagesAsync(storage, count);
            }
        }
        finally
        {
            await storage.Session.ReleaseGlobalLockAsync(TransportConstants.IncomingMessageLockId);
        }

        if (rescheduleImmediately)
        {
            agent.RescheduleIncomingRecovery();
        }
    }

    internal async Task<bool> TryRecoverIncomingMessagesAsync(IEnvelopePersistence storage, IncomingCount count)
    {
        var listener = _runtime.Endpoints.FindListeningAgent(count.Destination) ??
                       _runtime.Endpoints.FindListeningAgent(TransportConstants.Durable);

        if (listener!.Status != ListeningStatus.Accepting)
        {
            return false;
        }

        bool shouldFetchMore = false;

        await storage.Session.BeginAsync();

        try
        {
            var pageSize = _settings.RecoveryBatchSize;
            if (pageSize + listener.QueueCount > listener.Endpoint.BufferingLimits.Maximum)
            {
                pageSize = listener.Endpoint.BufferingLimits.Maximum - listener.QueueCount - 1;
            }

            shouldFetchMore = pageSize < count.Count;

            var envelopes = await storage.LoadPageOfGloballyOwnedIncomingAsync(count.Destination, pageSize);
            await storage.ReassignIncomingAsync(_settings.UniqueNodeId, envelopes);

            await storage.Session.CommitAsync();

            listener.EnqueueDirectly(envelopes);
            _logger.RecoveredIncoming(envelopes);
        }
        catch (Exception e)
        {
            await storage.Session.RollbackAsync();
            _logger.LogError(e, "Error trying to recover incoming envelopes");
        }

        return shouldFetchMore;
    }

    public string Description => "Recover persisted incoming messages";
}


