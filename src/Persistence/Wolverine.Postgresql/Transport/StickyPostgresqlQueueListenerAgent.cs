using Wolverine.Runtime;
using Wolverine.Runtime.Agents;

namespace Wolverine.Postgresql.Transport;

internal class StickyPostgresqlQueueListenerAgent : IAgent
{
    private readonly IWolverineRuntime _runtime;
    private readonly string _queue;
    private readonly string _databaseName;
    private TenantedPostgresqlQueue? _tenantEndpoint;

    public StickyPostgresqlQueueListenerAgent(IWolverineRuntime runtime, string queue, string databaseName)
    {
        _runtime = runtime;
        _queue = queue;
        _databaseName = databaseName;

        Uri = new Uri($"{StickyPostgresqlQueueListenerAgentFamily.StickyListenerSchema}://{_queue}/{_databaseName}");
    }
    
    public AgentStatus Status { get; set; } = AgentStatus.Started;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _tenantEndpoint ??= await findOrBuildEndpoint();

        await _runtime.Endpoints.StartListenerAsync(_tenantEndpoint, cancellationToken);
    }

    private async Task<TenantedPostgresqlQueue> findOrBuildEndpoint()
    {
        var transport = _runtime.Options.Transports.GetOrCreate<PostgresqlTransport>();

        if (transport.Databases == null)
            throw new InvalidOperationException("This system is not using multi-tenancy by database");

        var queue = transport.Queues[_queue];

        var database = (PostgresqlMessageStore)await transport.Databases.GetDatabaseAsync(_databaseName);

        var tenantEndpoint = new TenantedPostgresqlQueue(queue, database.NpgsqlDataSource, _databaseName);
        return tenantEndpoint;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _tenantEndpoint ??= await findOrBuildEndpoint();
        await _runtime.Endpoints.StopListenerAsync(_tenantEndpoint, cancellationToken);
        Status = AgentStatus.Stopped;
    }

    public Uri Uri { get; }
}