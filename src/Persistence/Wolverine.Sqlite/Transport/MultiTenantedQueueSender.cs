using ImTools;
using JasperFx.Core;
using Wolverine.RDBMS;
using MultiTenantedMessageStore = Wolverine.Persistence.Durability.MultiTenantedMessageStore;

namespace Wolverine.Sqlite.Transport;

internal class MultiTenantedQueueSender : ISqliteQueueSender, IAsyncDisposable
{
    private readonly SqliteQueue _queue;
    private readonly SqliteQueueSender _master;
    private readonly MultiTenantedMessageStore _stores;
    private ImHashMap<string, ISqliteQueueSender> _byDatabase = ImHashMap<string, ISqliteQueueSender>.Empty;
    private readonly SemaphoreSlim _lock = new(1);
    private readonly CancellationTokenSource _cancellation = new();

    public MultiTenantedQueueSender(SqliteQueue queue, MultiTenantedMessageStore stores)
    {
        _queue = queue;
        _master = new SqliteQueueSender(queue);
        _stores = stores;

        Destination = _queue.Uri;
    }

    public bool SupportsNativeScheduledSend => true;
    public Uri Destination { get; }

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

    private async ValueTask<ISqliteQueueSender> resolveSender(Envelope envelope)
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
                // but does not have the same name as the tenant id.
            }
            else
            {
                var sqliteStore = (SqliteMessageStore)database;
                sender = new SqliteQueueSender(_queue, sqliteStore.DataSource, database.Name);
                _byDatabase = _byDatabase.AddOrUpdate(database.Name, sender);

                if (_queue.Parent.AutoProvision)
                {
                    await _queue.EnsureSchemaExists(database.Name, sqliteStore.DataSource);
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
