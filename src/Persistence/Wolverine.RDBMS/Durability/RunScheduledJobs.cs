using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Wolverine.Logging;
using Wolverine.Persistence.Durability;
using Wolverine.Transports;

namespace Wolverine.RDBMS.Durability;

internal class RunScheduledJobs : IDurabilityAction
{
    private readonly ILogger _logger;
    private readonly AdvancedSettings _settings;

    public RunScheduledJobs(AdvancedSettings settings, ILogger logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public Task ExecuteAsync(IMessageStore storage, IDurabilityAgent agent)
    {
        var utcNow = DateTimeOffset.UtcNow;
        return ExecuteAtTimeAsync(storage, agent, utcNow);
    }

    public string Description { get; } = "Run Scheduled Messages";

    public async Task ExecuteAtTimeAsync(IMessageStore storage, IDurabilityAgent agent,
        DateTimeOffset utcNow)
    {
        await storage.Session.BeginAsync();

        var hasLock = await storage.Session.TryGetGlobalLockAsync(TransportConstants.ScheduledJobLockId);
        if (!hasLock)
        {
            await storage.Session.RollbackAsync();
            return;
        }


        try
        {
#pragma warning disable CS8600
            IReadOnlyList<Envelope> readyToExecute;
#pragma warning restore CS8600

            try
            {
                readyToExecute = await storage.LoadScheduledToExecuteAsync(utcNow);

                if (!readyToExecute.Any())
                {
                    await storage.Session.RollbackAsync();
                    return;
                }

                await storage.ReassignIncomingAsync(_settings.UniqueNodeId, readyToExecute);

                await storage.Session.CommitAsync();
            }
            catch (Exception)
            {
                await storage.Session.RollbackAsync();
                throw;
            }

            _logger.ScheduledJobsQueuedForExecution(readyToExecute);

            foreach (var envelope in readyToExecute) agent.EnqueueLocally(envelope);
        }
        finally
        {
            await storage.Session.ReleaseGlobalLockAsync(TransportConstants.ScheduledJobLockId);
        }
    }
}