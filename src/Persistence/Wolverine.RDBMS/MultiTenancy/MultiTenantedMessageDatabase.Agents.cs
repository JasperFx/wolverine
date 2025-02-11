using JasperFx.Core;
using Wolverine.Persistence;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;

namespace Wolverine.RDBMS.MultiTenancy;

public partial class MultiTenantedMessageDatabase : IAgentFamily
{
    public string Scheme => PersistenceConstants.AgentScheme;

    public async ValueTask<IReadOnlyList<Uri>> AllKnownAgentsAsync()
    {
        await _databases.RefreshAsync();
        var uris = databases().Select(x => new Uri($"{Scheme}://{x.Name}")).ToList();
        return uris;
    }

    public string Name => "Main";

    public async ValueTask<IAgent> BuildAgentAsync(Uri uri, IWolverineRuntime wolverineRuntime)
    {
        // This is checking what's already in memory
        var database = databases().FirstOrDefault(x => x.Name.EqualsIgnoreCase(uri.Host));
        if (database == null)
        {
            // Try to refresh in case it was recently added
            await _databases.RefreshAsync();
            database = databases().FirstOrDefault(x => x.Name.EqualsIgnoreCase(uri.Host));

            if (database == null)
            {
                throw new ArgumentOutOfRangeException(nameof(uri), "Unknown database " + uri.Host);
            }
        }

        return new DurabilityAgent(database.Name, _runtime, (IMessageDatabase)database)
        {
            AutoStartScheduledJobPolling = true, 
            Uri = uri
        };
    }

    public ValueTask<IReadOnlyList<Uri>> SupportedAgentsAsync()
    {
        var uris = databases().Select(x => new Uri($"{Scheme}://{x.Name}")).ToList();
        return new ValueTask<IReadOnlyList<Uri>>(uris);
    }

    public ValueTask EvaluateAssignmentsAsync(AssignmentGrid assignments)
    {
        assignments.DistributeEvenly(Scheme);
        return ValueTask.CompletedTask;
    }

    public IAgent StartScheduledJobs(IWolverineRuntime runtime)
    {
        return new DummyAgent();
    }

    private class DummyAgent : IAgent
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            Status = AgentStatus.Stopped;
            return Task.CompletedTask;
        }
        
        public AgentStatus Status { get; set; } = AgentStatus.Started;

        public Uri Uri { get; } = new Uri("dummy://");
    }
}