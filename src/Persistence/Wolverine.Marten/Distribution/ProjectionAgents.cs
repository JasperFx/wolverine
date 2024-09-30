using JasperFx.Core;
using JasperFx.Core.Reflection;
using Marten;
using Marten.Events.Daemon;
using Marten.Events.Daemon.Coordination;
using Marten.Storage;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;

namespace Wolverine.Marten.Distribution;

internal class ProjectionAgents : IStaticAgentFamily
{
    public const string SchemeName = "event-subscriptions";
    
    private readonly IDocumentStore _store;
    private readonly IProjectionCoordinator _coordinator;

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

    public ProjectionAgents(IDocumentStore store, IProjectionCoordinator coordinator)
    {
        _store = store;
        _coordinator = coordinator;
    }

    public ValueTask<IReadOnlyList<Uri>> AllKnownAgentsAsync()
    {
        return SupportedAgentsAsync();
    }

    private IEnumerable<Uri> allAgentUris(IReadOnlyList<IMartenDatabase> databases, IReadOnlyList<AsyncProjectionShard> shards)
    {
        foreach (var database in databases)
        {
            foreach (var shard in shards)
            {
                yield return UriFor(database.Identifier, shard.Name.Identity);
            }
        }
    }

    public ValueTask<IAgent> BuildAgentAsync(Uri uri, IWolverineRuntime wolverineRuntime)
    {
        return new ValueTask<IAgent>(new ProjectionAgent(uri, _coordinator));
    }

    public async ValueTask<IReadOnlyList<Uri>> SupportedAgentsAsync()
    {
        var databases = await _store.Storage.AllDatabases();
        var shards = _store.As<DocumentStore>().Options.Projections.AllShards();

        return allAgentUris(databases, shards).ToList();
    }

    public ValueTask EvaluateAssignmentsAsync(AssignmentGrid assignments)
    {
        assignments.DistributeEvenlyWithBlueGreenSemantics(SchemeName);
        return new ValueTask();
    }

    public string Scheme => SchemeName;
}