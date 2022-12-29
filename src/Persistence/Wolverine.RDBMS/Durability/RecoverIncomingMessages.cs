using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Weasel.Core;
using Wolverine.Configuration;
using Wolverine.Logging;
using Wolverine.Persistence.Durability;
using Wolverine.Transports;

namespace Wolverine.RDBMS.Durability;

internal class RecoverIncomingMessages : IDurabilityAction
{
    private readonly IEndpointCollection _endpoints;
    private readonly ILogger _logger;

    public RecoverIncomingMessages(ILogger logger, IEndpointCollection endpoints)
    {
        _logger = logger;
        _endpoints = endpoints;
    }

    public async Task ExecuteAsync(IMessageStore storage, IDurabilityAgent agent, NodeSettings nodeSettings,
        DatabaseSettings databaseSettings)
    {
        var rescheduleImmediately = false;

        await storage.Session.WithinSessionGlobalLockAsync(TransportConstants.IncomingMessageLockId, async () =>
        {
            var counts = await LoadAtLargeIncomingCountsAsync(storage.Session, databaseSettings);
            foreach (var count in counts)
            {
                rescheduleImmediately = rescheduleImmediately || await TryRecoverIncomingMessagesAsync(storage, count, nodeSettings);
            }
        });


        if (rescheduleImmediately)
        {
            agent.RescheduleIncomingRecovery();
        }
    }
    
    public static Task<IReadOnlyList<IncomingCount>> LoadAtLargeIncomingCountsAsync(IDurableStorageSession session, DatabaseSettings databaseSettings)
    {
        var sql = $"select {DatabaseConstants.ReceivedAt}, count(*) from {databaseSettings.SchemaName}.{DatabaseConstants.IncomingTable} where {DatabaseConstants.Status} = '{EnvelopeStatus.Incoming}' and {DatabaseConstants.OwnerId} = {TransportConstants.AnyNode} group by {DatabaseConstants.ReceivedAt}";
        
        return session.CreateCommand(sql).FetchList(async reader =>
        {
            var address = new Uri(await reader.GetFieldValueAsync<string>(0, session.Cancellation).ConfigureAwait(false));
            var count = await reader.GetFieldValueAsync<int>(1, session.Cancellation).ConfigureAwait(false);

            return new IncomingCount(address, count);
        }, session.Cancellation);
    }

    public string Description => "Recover persisted incoming messages";

    public virtual int DeterminePageSize(IListenerCircuit listener, IncomingCount count,
        NodeSettings nodeSettings)
    {
        if (listener!.Status != ListeningStatus.Accepting)
        {
            return 0;
        }

        var pageSize = nodeSettings.RecoveryBatchSize;
        if (pageSize > count.Count)
        {
            pageSize = count.Count;
        }

        if (pageSize + listener.QueueCount > listener.Endpoint.BufferingLimits.Maximum)
        {
            pageSize = listener.Endpoint.BufferingLimits.Maximum - listener.QueueCount - 1;
        }

        if (pageSize < 0)
        {
            return 0;
        }

        return pageSize;
    }

    internal async Task<bool> TryRecoverIncomingMessagesAsync(IMessageStore storage, IncomingCount count,
        NodeSettings nodeSettings)
    {
        var listener = findListenerCircuit(count);

        var pageSize = DeterminePageSize(listener!, count, nodeSettings);

        if (pageSize <= 0)
        {
            return false;
        }

        await RecoverMessagesAsync(storage, count, pageSize, listener!, nodeSettings).ConfigureAwait(false);

        // Reschedule again if it wasn't able to grab all outstanding envelopes
        return pageSize < count.Count;
    }

    private IListenerCircuit findListenerCircuit(IncomingCount count)
    {
        if (count.Destination.Scheme == TransportConstants.Local)
        {
            return (IListenerCircuit)_endpoints.GetOrBuildSendingAgent(count.Destination);
        }

        var listener = _endpoints.FindListeningAgent(count.Destination) ??
                       _endpoints.FindListeningAgent(TransportConstants.Durable);
        return listener!;
    }

    public virtual async Task RecoverMessagesAsync(IMessageStore storage, IncomingCount count, int pageSize,
        IListenerCircuit listener, NodeSettings nodeSettings)
    {
        await storage.Session.BeginAsync();

        try
        {
            var envelopes = await storage.LoadPageOfGloballyOwnedIncomingAsync(count.Destination, pageSize);
            await storage.ReassignIncomingAsync(nodeSettings.UniqueNodeId, envelopes);

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