using Microsoft.Extensions.Logging;
using Wolverine.Logging;
using Wolverine.Persistence.Durability;
using Wolverine.Transports;

namespace Wolverine.RDBMS.Durability;

internal class RunScheduledJobs : IDurabilityAction
{
    private readonly ILogger _logger;
    private readonly DurabilitySettings _settings;

    public RunScheduledJobs(DurabilitySettings settings, ILogger logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public Task ExecuteAsync(IMessageDatabase database, IDurabilityAgent agent,
        IDurableStorageSession session)
    {
        var utcNow = DateTimeOffset.UtcNow;
        return ExecuteAtTimeAsync(database, session, agent, utcNow);
    }

    public string Description { get; } = "Run Scheduled Messages";

    public async Task ExecuteAtTimeAsync(IMessageDatabase database, IDurableStorageSession session,
        IDurabilityAgent agent,
        DateTimeOffset utcNow)
    {
        await session.BeginAsync();

        var hasLock = await session.TryGetGlobalLockAsync(TransportConstants.ScheduledJobLockId);
        if (!hasLock)
        {
            await session.RollbackAsync();
            return;
        }


        try
        {
#pragma warning disable CS8600
            IReadOnlyList<Envelope> readyToExecute;
#pragma warning restore CS8600

            try
            {
                readyToExecute = await database.LoadScheduledToExecuteAsync(utcNow);

                if (!readyToExecute.Any())
                {
                    await session.RollbackAsync();
                    return;
                }

                await database.ReassignIncomingAsync(_settings.NodeLockId, readyToExecute);

                await session.CommitAsync();
            }
            catch (Exception)
            {
                await session.RollbackAsync();
                throw;
            }

            _logger.ScheduledJobsQueuedForExecution(readyToExecute);

            foreach (var envelope in readyToExecute) agent.EnqueueLocally(envelope);
        }
        finally
        {
            await session.ReleaseGlobalLockAsync(TransportConstants.ScheduledJobLockId);
        }
    }
}