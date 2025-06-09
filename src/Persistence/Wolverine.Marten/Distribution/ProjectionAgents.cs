using ImTools;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Marten;
using Marten.Events.Daemon.Coordination;
using Marten.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using AgentStatus = Wolverine.Runtime.Agents.AgentStatus;

namespace Wolverine.Marten.Distribution;

internal class ProjectionAgents : IStaticAgentFamily, IProjectionCoordinator
{
    public const string SchemeName = "event-subscriptions";
    
    private readonly IDocumentStore _store;
    private readonly IProjectionCoordinator _coordinator;
    private ImHashMap<Uri, ProjectionAgent> _agents = ImHashMap<Uri, ProjectionAgent>.Empty;
    private readonly object _agentLocker = new();

    public static Uri UriFor(string databaseName, string shardName)
    {
        return $"{SchemeName}://{databaseName}/{shardName}".ToUri();
    }

    public static (string DatabaseName, string ProjectionName) Parse(Uri uri)
    {
        if (uri.Scheme != SchemeName)
            throw new ArgumentOutOfRangeException(nameof(uri), $"{uri} is not a {SchemeName} Uri");

        return (uri.Host, uri.Segments.Last().Trim('/'));

    }

    public ProjectionAgents(IDocumentStore store, ILogger<ProjectionCoordinator> logger)
    {
        _store = store;
        _coordinator = new ProjectionCoordinator(store, logger);
    }

    public ValueTask<IReadOnlyList<Uri>> AllKnownAgentsAsync()
    {
        return SupportedAgentsAsync();
    }

    private IEnumerable<Uri> allAgentUris(IReadOnlyList<IMartenDatabase> databases, ShardName[] shards)
    {
        foreach (var database in databases)
        {
            foreach (var shard in shards)
            {
                yield return UriFor(database.Identifier, shard.Identity);
            }
        }
    }
    
    public ValueTask<IAgent> BuildAgentAsync(Uri uri, IWolverineRuntime wolverineRuntime)
    {
        if (_agents.TryFind(uri, out var agent))
        {
            return new ValueTask<IAgent>(agent);
        }

        lock (_agentLocker)
        {
            if (_agents.TryFind(uri, out agent))
            {
                return new ValueTask<IAgent>(agent);
            }
            
            agent = new ProjectionAgent(uri, _coordinator);
            _agents = _agents.AddOrUpdate(uri, agent);
        }

        return new ValueTask<IAgent>(agent);
    }

    public async ValueTask<IReadOnlyList<Uri>> SupportedAgentsAsync()
    {
        var databases = await _store.Storage.AllDatabases();
        var shardNames = _store.As<DocumentStore>().Options.Projections.AllShards().Select(x => x.Name).ToArray();

        return allAgentUris(databases, shardNames).ToList();
    }

    public ValueTask EvaluateAssignmentsAsync(AssignmentGrid assignments)
    {
        assignments.DistributeEvenlyWithBlueGreenSemantics(SchemeName);
        return new ValueTask();
    }

    public string Scheme => SchemeName;

    Task IHostedService.StartAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    Task IHostedService.StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    IProjectionDaemon IProjectionCoordinator.DaemonForMainDatabase()
    {
        return _coordinator.DaemonForMainDatabase();
    }

    ValueTask<IProjectionDaemon> IProjectionCoordinator.DaemonForDatabase(string databaseIdentifier)
    {
        return _coordinator.DaemonForDatabase(databaseIdentifier);
    }

    async Task IProjectionCoordinator.PauseAsync()
    {
        var active = _agents.Enumerate().Select(x => x.Value)
            .Where(x => x.Status == AgentStatus.Started).ToArray();

        foreach (var agent in active)
        {
            await agent.PauseAsync(CancellationToken.None);
        }
    }

    async Task IProjectionCoordinator.ResumeAsync()
    {
        var paused = _agents.Enumerate().Select(x => x.Value)
            .Where(x => x.Status == AgentStatus.Paused).ToArray();

        foreach (var agent in paused)
        {
            await agent.StartAsync(CancellationToken.None);
        }
    }
}