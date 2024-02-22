using JasperFx.Core;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;

namespace Wolverine.RDBMS.MultiTenancy;

public partial class MultiTenantedMessageDatabase : IAgentFamily
{
    public string Scheme { get; } = DurabilityAgent.AgentScheme;

    public async ValueTask<IReadOnlyList<Uri>> AllKnownAgentsAsync()
    {
        await _databases.RefreshAsync();
        var uris = databases().Select(x => new Uri($"{Scheme}://{x.Name}")).ToList();
        return uris;
    }

    public ValueTask<IAgent> BuildAgentAsync(Uri uri, IWolverineRuntime wolverineRuntime)
    {
        var database = databases().FirstOrDefault(x => x.Name.EqualsIgnoreCase(uri.Host));
        if (database == null)
        {
            throw new ArgumentOutOfRangeException(nameof(uri), "Unknown database " + uri.Host);
        }

        return new ValueTask<IAgent>(new DurabilityAgent(database.Name, _runtime, database){AutoStartScheduledJobPolling = true});
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
            return Task.CompletedTask;
        }

        public Uri Uri { get; } = new Uri("dummy://");
    }
}