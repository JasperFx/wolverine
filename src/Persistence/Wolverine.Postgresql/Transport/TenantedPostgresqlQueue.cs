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
    private PostgresqlQueueSender _sender;

    public TenantedPostgresqlQueue(PostgresqlQueue parent, NpgsqlDataSource dataSource, string databaseName) : base(PostgresqlQueue.ToUri(parent.Name, databaseName), EndpointRole.Application)
    {
        _parent = parent;
        _dataSource = dataSource;
        _databaseName = databaseName;
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
}