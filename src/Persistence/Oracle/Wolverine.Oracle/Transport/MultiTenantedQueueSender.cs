using ImTools;
using JasperFx.Core;
using Wolverine.RDBMS;
using MultiTenantedMessageStore = Wolverine.Persistence.Durability.MultiTenantedMessageStore;

namespace Wolverine.Oracle.Transport;

internal class MultiTenantedQueueSender : IOracleQueueSender, IAsyncDisposable
{
    private readonly OracleQueue _queue;
    private readonly OracleQueueSender _master;
    private readonly MultiTenantedMessageStore _stores;
    private ImHashMap<string, IOracleQueueSender> _byDatabase = ImHashMap<string, IOracleQueueSender>.Empty;
    private readonly SemaphoreSlim _lock = new(1);
    private readonly CancellationTokenSource _cancellation = new();

    public MultiTenantedQueueSender(OracleQueue queue, MultiTenantedMessageStore stores)
    {
        _queue = queue;
        _master = new OracleQueueSender(queue);
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

    private async ValueTask<IOracleQueueSender> resolveSender(Envelope envelope)
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
                // Database encountered before with a different tenant id
            }
            else
            {
                var oracleDataSource = (OracleDataSource)database.DataSource;
                sender = new OracleQueueSender(_queue, oracleDataSource, database.Name);
                _byDatabase = _byDatabase.AddOrUpdate(database.Name, sender);

                if (_queue.Parent.AutoProvision)
                {
                    await _queue.EnsureSchemaExists(database.Name, oracleDataSource);
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
