using ImTools;
using JasperFx.Core;
using MySqlConnector;
using Wolverine.RDBMS;
using MultiTenantedMessageStore = Wolverine.Persistence.Durability.MultiTenantedMessageStore;

namespace Wolverine.MySql.Transport;

internal class MultiTenantedQueueSender : IMySqlQueueSender, IAsyncDisposable
{
    private readonly MySqlQueue _queue;
    private readonly MySqlQueueSender _master;
    private readonly MultiTenantedMessageStore _stores;
    private ImHashMap<string, IMySqlQueueSender> _byDatabase = ImHashMap<string, IMySqlQueueSender>.Empty;
    private readonly SemaphoreSlim _lock = new(1);
    private readonly CancellationTokenSource _cancellation = new();

    public MultiTenantedQueueSender(MySqlQueue queue, MultiTenantedMessageStore stores)
    {
        _queue = queue;
        _master = new MySqlQueueSender(queue);
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

    private async ValueTask<IMySqlQueueSender> resolveSender(Envelope envelope)
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
                var mySqlDataSource = (MySqlDataSource)database.DataSource;
                sender = new MySqlQueueSender(_queue, mySqlDataSource, database.Name);
                _byDatabase = _byDatabase.AddOrUpdate(database.Name, sender);

                if (_queue.Parent.AutoProvision)
                {
                    await _queue.EnsureSchemaExists(database.Name, mySqlDataSource);
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
