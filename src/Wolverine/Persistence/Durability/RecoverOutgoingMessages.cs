using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Wolverine.Logging;
using Microsoft.Extensions.Logging;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.Persistence.Durability;

public class RecoverOutgoingMessages : IMessagingAction
{
    private readonly ILogger _logger;
    private readonly IWolverineRuntime _runtime;
    private readonly AdvancedSettings _settings;

    public RecoverOutgoingMessages(IWolverineRuntime runtime, AdvancedSettings settings, ILogger logger)
    {
        _runtime = runtime;
        _settings = settings;
        _logger = logger;
    }

    public string Description { get; } = "Recover persisted outgoing messages";

    public async Task ExecuteAsync(IEnvelopePersistence storage, IDurabilityAgent agent)
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
                var found = await recoverFromAsync(destination, storage);

                count += found;
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


    private async Task<int> recoverFromAsync(Uri destination, IEnvelopePersistence storage)
    {
        try
        {
#pragma warning disable CS8600
            Envelope[] filtered;
            IReadOnlyList<Envelope> outgoing;
#pragma warning restore CS8600
            if (_runtime.GetOrBuildSendingAgent(destination).Latched)
            {
                return 0;
            }

            await storage.Session.BeginAsync();

            try
            {
                outgoing = await storage.LoadOutgoingAsync(destination);

                var expiredMessages = outgoing.Where(x => x.IsExpired()).ToArray();
                _logger.DiscardedExpired(expiredMessages);


                await storage.DeleteOutgoingAsync(expiredMessages.ToArray());
                filtered = outgoing.Where(x => !expiredMessages.Contains(x)).ToArray();

                // Might easily try to do this in the time between starting
                // and having the data fetched. Was able to make that happen in
                // (contrived) testing
                if (_runtime.GetOrBuildSendingAgent(destination).Latched || !filtered.Any())
                {
                    await storage.Session.RollbackAsync();
                    return 0;
                }

                await storage.ReassignOutgoingAsync(_settings.UniqueNodeId, filtered);


                await storage.Session.CommitAsync();
            }
            catch (Exception)
            {
                await storage.Session.RollbackAsync();
                throw;
            }

            _logger.RecoveredOutgoing(filtered);

            foreach (var envelope in filtered)
            {
                try
                {
                    await _runtime.GetOrBuildSendingAgent(destination).EnqueueOutgoingAsync(envelope);
                }
                catch (Exception? e)
                {
                    _logger.LogError(e, "Unable to enqueue {Envelope} for sending", envelope);
                }
            }

            return outgoing.Count();
        }
        catch (UnknownTransportException? e)
        {
            _logger.LogError(e, "Could not resolve a channel for {Destination}", destination);

            await storage.Session.BeginAsync();

            await storage.DeleteByDestinationAsync(destination);
            await storage.Session.CommitAsync();

            return 0;
        }
    }
}
