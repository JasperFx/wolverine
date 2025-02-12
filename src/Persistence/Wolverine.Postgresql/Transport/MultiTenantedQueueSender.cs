using JasperFx.Core;
using Npgsql;
using Wolverine.RDBMS;
using Wolverine.RDBMS.MultiTenancy;
using MultiTenantedMessageStore = Wolverine.Persistence.Durability.MultiTenantedMessageStore;

namespace Wolverine.Postgresql.Transport;

internal class MultiTenantedQueueSender : IPostgresqlQueueSender, IAsyncDisposable
{
    private readonly PostgresqlQueue _queue;
    private readonly PostgresqlQueueSender _master;
    private readonly MultiTenantedMessageStore _stores;
    private ImHashMap<string, IPostgresqlQueueSender> _byDatabase = ImHashMap<string, IPostgresqlQueueSender>.Empty;
    private readonly SemaphoreSlim _lock = new(1);
    private readonly CancellationTokenSource _cancellation = new();

    public MultiTenantedQueueSender(PostgresqlQueue queue, MultiTenantedMessageStore stores)
    {
        _queue = queue;
        _master = new PostgresqlQueueSender(queue);
        _stores = stores;

        Destination = _queue.Uri;
    }

    public bool SupportsNativeScheduledSend => true;
    public Uri Destination { get;  }
    public Task<bool> PingAsync()
    {
        return _master.PingAsync();
    }

    public async ValueTask SendAsync(Envelope envelope)
    {
        var sender = await resolveSender(envelope);
        await sender.SendAsync(envelope);
    }

    public async Task ScheduleRetryAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        var sender = await resolveSender(envelope);
        await sender.ScheduleRetryAsync(envelope, cancellationToken);
    }

    private async ValueTask<IPostgresqlQueueSender> resolveSender(Envelope envelope)
    {
        if (envelope.TenantId.IsEmpty() || envelope.TenantId == "*DEFAULT*")
        {
            return _master;
        }

        if (_byDatabase.TryFind(envelope.TenantId, out var sender))
        {
            return sender;
        }

        await _lock.WaitAsync(_cancellation.Token);
        try
        {
            var database = (IMessageDatabase)await _stores.GetDatabaseAsync(envelope.TenantId);
            if (_byDatabase.TryFind(database.Name, out sender))
            {
                // This indicates that the database has been encountered before,
                // but does not have the same name as the tenant id. This is possible
                // in multi-level multi-tenancy
            }
            else
            {
                var npgsqlDataSource = (NpgsqlDataSource)database.DataSource;
                sender = new PostgresqlQueueSender(_queue, npgsqlDataSource, database.Name);
                _byDatabase = _byDatabase.AddOrUpdate(database.Name, sender);

                if (_queue.Parent.AutoProvision)
                {
                    await _queue.EnsureSchemaExists(database.Name, npgsqlDataSource);
                }
            }

            _byDatabase = _byDatabase.AddOrUpdate(envelope.TenantId, sender);
        }
        finally
        {
            _lock.Release();
        }

        return sender;
    }

    public ValueTask DisposeAsync()
    {
        _cancellation.Cancel();
        return new ValueTask();
    }
}