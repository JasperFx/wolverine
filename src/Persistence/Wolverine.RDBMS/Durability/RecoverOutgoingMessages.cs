using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Weasel.Core;
using Wolverine.Logging;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.RDBMS.Durability;

internal class RecoverOutgoingMessages : IDurabilityAction
{
    private readonly ILogger _logger;
    private readonly DatabaseSettings _databaseSettings;
    private readonly IWolverineRuntime _runtime;
    private readonly AdvancedSettings _settings;
    private readonly CancellationToken _cancellation;

    public RecoverOutgoingMessages(IWolverineRuntime runtime, AdvancedSettings settings, ILogger logger,
        DatabaseSettings databaseSettings)
    {
        _runtime = runtime;
        _settings = settings;
        _logger = logger;
        _databaseSettings = databaseSettings;
        _cancellation = runtime.Cancellation;
    }

    public string Description => "Recover persisted outgoing messages";

    public async Task ExecuteAsync(IMessageStore storage, IDurabilityAgent agent)
    {
        var hasLock = await storage.Session.TryGetGlobalLockAsync(TransportConstants.OutgoingMessageLockId);
        if (!hasLock)
        {
            return;
        }

        try
        {
            var destinations = await storage.FindAllDestinationsAsync();

            var count = 0;
            foreach (var destination in destinations)
            {
                try
                {
                    var sendingAgent = _runtime.Endpoints.GetOrBuildSendingAgent(destination);

                    if (sendingAgent.Latched)
                    {
                        break;
                    }

                    var found = await recoverFromAsync(sendingAgent, storage);

                    count += found;
                }
                catch (UnknownTransportException e)
                {
                    _logger.LogError(e, "Could not resolve a channel for {Destination}. Deleting outgoing messages",
                        destination);

                    await storage.Session.BeginAsync();

                    await DeleteByDestinationAsync(storage.Session, destination);
                    await storage.Session.CommitAsync();
                    break;
                }
            }

            var wasMaxedOut = count >= _settings.RecoveryBatchSize;

            if (wasMaxedOut)
            {
                agent.RescheduleOutgoingRecovery();
            }
        }
        finally
        {
            await storage.Session.ReleaseGlobalLockAsync(TransportConstants.OutgoingMessageLockId);
        }
    }
    
    internal Task DeleteByDestinationAsync(IDurableStorageSession session, Uri? destination)
    {
        if (session.Transaction == null)
        {
            throw new InvalidOperationException("No current transaction");
        }

        return session.Transaction
            .CreateCommand(
                $"delete from {_databaseSettings.SchemaName}.{DatabaseConstants.OutgoingTable} where owner_id = :owner and destination = @destination")
            .With("destination", destination!.ToString())
            .With("owner", TransportConstants.AnyNode)
            .ExecuteNonQueryAsync(_cancellation);
    }


    private async Task<int> recoverFromAsync(ISendingAgent sendingAgent, IMessageStore storage)
    {
#pragma warning disable CS8600
        Envelope[] filtered;
        IReadOnlyList<Envelope> outgoing;
#pragma warning restore CS8600

        try
        {
            await storage.Session.BeginAsync();

            outgoing = await storage.LoadOutgoingAsync(sendingAgent.Destination);

            var expiredMessages = outgoing.Where(x => x.IsExpired()).ToArray();
            _logger.DiscardedExpired(expiredMessages);


            await storage.DeleteOutgoingAsync(expiredMessages.ToArray());
            filtered = outgoing.Where(x => !expiredMessages.Contains(x)).ToArray();

            // Might easily try to do this in the time between starting
            // and having the data fetched. Was able to make that happen in
            // (contrived) testing
            if (sendingAgent.Latched || !filtered.Any())
            {
                await storage.Session.RollbackAsync();
                return 0;
            }

            await storage.ReassignOutgoingAsync(_settings.UniqueNodeId, filtered);

            await storage.Session.CommitAsync();
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error while trying to recover persisted, outgoing envelopes to {Uri}",
                sendingAgent.Destination);
            await storage.Session.RollbackAsync();
            throw;
        }

        _logger.RecoveredOutgoing(filtered);

        foreach (var envelope in filtered)
        {
            try
            {
                await sendingAgent.EnqueueOutgoingAsync(envelope);
            }
            catch (Exception? e)
            {
                _logger.LogError(e, "Unable to enqueue {Envelope} for sending", envelope);
            }
        }

        return outgoing.Count();
    }
}