using JasperFx;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;

namespace Wolverine.Persistence.Durability;

public partial class MultiTenantedMessageStore : IAgentFamily
{
    public string Scheme => PersistenceConstants.AgentScheme;

    public async ValueTask<IReadOnlyList<Uri>> AllKnownAgentsAsync()
    {
        await Source.RefreshAsync();
        var uris = databases().Select(x => x.Uri).ToList();
        return uris;
    }

    public async ValueTask<IAgent> BuildAgentAsync(Uri uri, IWolverineRuntime wolverineRuntime)
    {
        // This is checking what's already in memory
        var database = databases().FirstOrDefault(x => x.Uri == uri);
        if (database == null)
        {
            // Try to refresh in case it was recently added
            await Source.RefreshAsync();
            database = databases().FirstOrDefault(x => x.Uri == uri);

            if (database == null)
            {
                throw new ArgumentOutOfRangeException(nameof(uri), "Unknown database " + uri);
            }
        }

        if (database is IMessageStoreWithAgentSupport agentSupport)
        {
            return agentSupport.BuildAgent(wolverineRuntime);
        }

        throw new NotSupportedException($"The database identified as {uri} does not support durability agents");
    }

    public ValueTask<IReadOnlyList<Uri>> SupportedAgentsAsync()
    {
        var uris = databases().OfType<IMessageStoreWithAgentSupport>().Select(x => x.Uri).ToList();
        return new ValueTask<IReadOnlyList<Uri>>(uris);
    }

    public ValueTask EvaluateAssignmentsAsync(AssignmentGrid assignments)
    {
        // GH-3785: same cross-family database affinity as MessageStoreCollection — see the note there.
        var preference = DurabilityProjectionAffinity.BuildPreference(assignments);
        assignments.DistributeEvenlyWithAffinity(Scheme, preference.NodeFor);

        preference.ReportTo(_logger, ref _lastAffinityReport);

        return ValueTask.CompletedTask;
    }

    private (int Known, int Considered, int Matched) _lastAffinityReport;

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
        
        public AgentStatus Status { get; set; } = AgentStatus.Running;

        public Uri Uri { get; } = new Uri("wolverinedb://");
    }
}