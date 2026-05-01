using Npgsql;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.Postgresql.Transport;

internal class TenantedPostgresqlQueue : Endpoint, IDatabaseBackedEndpoint
{
    private readonly PostgresqlQueue _parent;
    private readonly NpgsqlDataSource _dataSource;
    private readonly string _databaseName;
    private PostgresqlQueueSender _sender = null!;

    public TenantedPostgresqlQueue(PostgresqlQueue parent, NpgsqlDataSource dataSource, string databaseName) : base(PostgresqlQueue.ToUri(parent.Name, databaseName), EndpointRole.Application)
    {
        _parent = parent;
        _dataSource = dataSource;
        _databaseName = databaseName;
        BrokerRole = "queue";
    }

    public override async ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver)
    {
        _sender = new PostgresqlQueueSender(_parent, _dataSource, _databaseName);
        var listener = new PostgresqlQueueListener(_parent, runtime, receiver, _dataSource, _databaseName);
        await listener.StartAsync();
        return listener;
    }

    protected override ISender CreateSender(IWolverineRuntime runtime)
    {
        throw new NotSupportedException();
    }

    public Task ScheduleRetryAsync(Envelope envelope, CancellationToken cancellation)
    {
        return _sender.ScheduleRetryAsync(envelope, cancellation);
    }

    /// <summary>
    /// Cheap connectivity ping against the per-tenant database. Used by
    /// <see cref="StickyPostgresqlQueueListenerAgent.CheckHealthAsync"/> to surface
    /// per-tenant DB reachability as a health signal.
    /// </summary>
    internal async Task PingDatabaseAsync(CancellationToken cancellationToken)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        try
        {
            var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1";
            await cmd.ExecuteScalarAsync(cancellationToken);
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    /// <summary>
    /// Returns the row count of the parent queue table on this tenant's database. Used
    /// by <see cref="StickyPostgresqlQueueListenerAgent.CheckHealthAsync"/> to surface
    /// per-tenant queue depth as a health signal.
    /// </summary>
    internal async Task<long> GetQueueDepthAsync(CancellationToken cancellationToken)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken);
        try
        {
            var cmd = conn.CreateCommand();
            cmd.CommandText = $"select count(*) from {_parent.QueueTable.Identifier}";
            var raw = await cmd.ExecuteScalarAsync(cancellationToken);
            return raw switch
            {
                long l => l,
                int i => i,
                _ => Convert.ToInt64(raw)
            };
        }
        finally
        {
            await conn.CloseAsync();
        }
    }
}