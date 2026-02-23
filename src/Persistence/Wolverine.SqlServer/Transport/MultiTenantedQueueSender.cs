using ImTools;
using JasperFx.Core;
using Wolverine.RDBMS;
using Wolverine.SqlServer.Persistence;
using MultiTenantedMessageStore = Wolverine.Persistence.Durability.MultiTenantedMessageStore;

namespace Wolverine.SqlServer.Transport;

internal class MultiTenantedQueueSender : ISqlServerQueueSender, IAsyncDisposable
{
    private readonly SqlServerQueue _queue;
    private readonly SqlServerQueueSender _master;
    private readonly MultiTenantedMessageStore _stores;
    private ImHashMap<string, ISqlServerQueueSender> _byDatabase = ImHashMap<string, ISqlServerQueueSender>.Empty;
    private readonly SemaphoreSlim _lock = new(1);
    private readonly CancellationTokenSource _cancellation = new();

    public MultiTenantedQueueSender(SqlServerQueue queue, MultiTenantedMessageStore stores)
    {
        _queue = queue;
        _master = new SqlServerQueueSender(queue);
        _stores = stores;

        Destination = _queue.Uri;
    }

    public bool SupportsNativeScheduledSend => true;
    public bool SupportsNativeScheduledCancellation => false;
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

    private async ValueTask<ISqlServerQueueSender> resolveSender(Envelope envelope)
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
                var sqlServerStore = (SqlServerMessageStore)database;
                sender = new SqlServerQueueSender(_queue, sqlServerStore.Settings.ConnectionString, database.Name);
                _byDatabase = _byDatabase.AddOrUpdate(database.Name, sender);

                if (_queue.Parent.AutoProvision)
                {
                    await _queue.EnsureSchemaExists(database.Name, sqlServerStore.Settings.ConnectionString);
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

    public Task CancelScheduledMessageAsync(object schedulingToken, CancellationToken cancellation = default)
    {
        throw new NotSupportedException(
            "Cancelling scheduled messages is not supported for multi-tenanted database queue endpoints. " +
            "Use a single-tenant endpoint or wait for future multi-tenant cancellation support.");
    }

    public ValueTask DisposeAsync()
    {
        _cancellation.Cancel();
        return new ValueTask();
    }
}
