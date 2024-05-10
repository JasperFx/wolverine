using Wolverine.Configuration;
using Wolverine.RDBMS.MultiTenancy;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;

namespace Wolverine.Postgresql.Transport;

public class StickyPostgresqlQueueListenerAgentFamily : IAgentFamily
{
    private readonly IWolverineRuntime _runtime;
    public static string StickyListenerSchema = "pg-queue-listener";
    private readonly MultiTenantedMessageDatabase _databases;
    private readonly PostgresqlQueue[] _queues;

    public StickyPostgresqlQueueListenerAgentFamily(IWolverineRuntime runtime)
    {
        _runtime = runtime;
        if (_runtime.Storage is MultiTenantedMessageDatabase databases)
        {
            _databases = databases;
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(runtime),
                $"The message storage is not {nameof(MultiTenantedMessageDatabase)}");
        }

        var transport = _runtime.Options.Transports.GetOrCreate<PostgresqlTransport>();
        _queues = transport.Queues.Where(x => x.IsListener && x.ListenerScope == ListenerScope.Exclusive).ToArray();
    }

    public string Scheme { get; set; } = StickyListenerSchema;
    public async ValueTask<IReadOnlyList<Uri>> AllKnownAgentsAsync()
    {
        var databases = (await _databases.CheckForDatabasesAsync(_runtime))
            .OfType<PostgresqlMessageStore>()
            .ToArray();

        var uris = databases.SelectMany(db =>
        {
            return _queues.Select(q => new Uri($"{Scheme}://{q.Name}/{db.Name}"));
        }).ToList();

        return uris;
    }

    public ValueTask<IAgent> BuildAgentAsync(Uri uri, IWolverineRuntime wolverineRuntime)
    {
        var queueName = uri.Host;
        var databaseName = uri.Segments.Last(x => x != "/").Trim('/');

        var agent = new StickyPostgresqlQueueListenerAgent(_runtime, queueName, databaseName);
        return ValueTask.FromResult<IAgent>(agent) ;
    }

    public ValueTask<IReadOnlyList<Uri>> SupportedAgentsAsync()
    {
        return AllKnownAgentsAsync();
    }

    public ValueTask EvaluateAssignmentsAsync(AssignmentGrid assignments)
    {
        assignments.DistributeEvenly(Scheme);
        return new ValueTask();
    }
}