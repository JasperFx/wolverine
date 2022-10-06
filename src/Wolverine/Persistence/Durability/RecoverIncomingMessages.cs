using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Logging;
using Wolverine.Transports;

namespace Wolverine.Persistence.Durability;

internal class RecoverIncomingMessages : IMessagingAction
{
    private readonly IEndpointCollection _endpoints;
    private readonly ILogger _logger;
    private readonly AdvancedSettings _settings;

    public RecoverIncomingMessages(AdvancedSettings settings,
        ILogger logger, IEndpointCollection endpoints)
    {
        _settings = settings;
        _logger = logger;
        _endpoints = endpoints;
    }

    public async Task ExecuteAsync(IEnvelopePersistence storage, IDurabilityAgent agent)
    {
        // We don't need this to be transactional
        var gotLock = await storage.Session.TryGetGlobalLockAsync(TransportConstants.IncomingMessageLockId);
        if (!gotLock)
        {
            return;
        }

        var rescheduleImmediately = false;

        try
        {
            var counts = await storage.LoadAtLargeIncomingCountsAsync();
            foreach (var count in counts)
                rescheduleImmediately = rescheduleImmediately || await TryRecoverIncomingMessagesAsync(storage, count);
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

    public string Description => "Recover persisted incoming messages";

    public virtual int DeterminePageSize(IListeningAgent listener, IncomingCount count)
    {
        if (listener!.Status != ListeningStatus.Accepting)
        {
            return 0;
        }

        var pageSize = _settings.RecoveryBatchSize;
        if (pageSize > count.Count)
        {
            pageSize = count.Count;
        }

        if (pageSize + listener.QueueCount > listener.Endpoint.BufferingLimits.Maximum)
        {
            pageSize = listener.Endpoint.BufferingLimits.Maximum - listener.QueueCount - 1;
        }

        if (pageSize < 0) return 0;
        
        return pageSize;
    }

    internal async Task<bool> TryRecoverIncomingMessagesAsync(IEnvelopePersistence storage, IncomingCount count)
    {
        var listener = _endpoints.FindListeningAgent(count.Destination) ??
                       _endpoints.FindListeningAgent(TransportConstants.Durable);

        var pageSize = DeterminePageSize(listener!, count);

        if (pageSize <= 0)
        {
            return false;
        }

        await RecoverMessagesAsync(storage, count, pageSize, listener!).ConfigureAwait(false);

        // Reschedule again if it wasn't able to grab all outstanding envelopes
        return pageSize < count.Count;
    }

    public virtual async Task RecoverMessagesAsync(IEnvelopePersistence storage, IncomingCount count, int pageSize,
        IListeningAgent listener)
    {
        await storage.Session.BeginAsync();

        try
        {
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
    }
}