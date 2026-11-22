using ImTools;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Wolverine.RDBMS.MultiTenancy;
using Wolverine.Runtime;
using Wolverine.Transports;
using IMessageStore = Wolverine.Persistence.Durability.IMessageStore;
using ITenantDatabaseListener = Wolverine.Persistence.Durability.ITenantDatabaseListener;
using MultiTenantedMessageStore = Wolverine.Persistence.Durability.MultiTenantedMessageStore;

namespace Wolverine.Postgresql.Transport;

public class MultiTenantedQueueListener : IListener, ITenantDatabaseListener
{
    private readonly ILogger _logger;
    private readonly PostgresqlQueue _queue;
    private readonly MultiTenantedMessageStore _stores;
    private readonly IWolverineRuntime _runtime;
    private readonly IReceiver _receiver;
    private readonly CancellationTokenSource _cancellation;

    private ImHashMap<string, PostgresqlQueueListener> _listeners = ImHashMap<string, PostgresqlQueueListener>.Empty;
    private Task? _activator;

    public MultiTenantedQueueListener(ILogger logger, PostgresqlQueue queue, MultiTenantedMessageStore stores, IWolverineRuntime runtime, IReceiver receiver)
    {
        _logger = logger;
        _queue = queue;
        _stores = stores;
        _runtime = runtime;
        _receiver = receiver;

        Address = _queue.Uri;

        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(runtime.Cancellation);
    }

    public async Task StartAsync()
    {
        if (_queue.Parent.Store != null)
        {
            await startListening(_queue.Parent.Store);
        }

        foreach (var store in _stores.Source.AllActive().OfType<PostgresqlMessageStore>())
        {
            await startListening(store);
        }

        _activator = Task.Run(async () =>
        {
            while (!_cancellation.IsCancellationRequested)
            {
                await Task.Delay(_runtime.Options.Durability.TenantCheckPeriod, _cancellation.Token);

                await _stores.Source.RefreshAsync();
                var databases = _stores.Source.AllActive();
                foreach (var store in databases.OfType<PostgresqlMessageStore>())
                {
                    if (!_listeners.Contains(store.Name))
                    {
                        await startListening(store);
                    }
                }
            }
        }, _cancellation.Token);
    }

    private async Task startListening(PostgresqlMessageStore store)
    {
        // GH-5817. Tenant ids that share one database each get their own tenant store, and every one of those
        // stores answers to the same name (the database URI). A queue listener is per database, so the second
        // store's listener would only replace the first in this map, and stop/dispose would never reach the
        // first. Listen once per database, exactly like the durability agents, which are per database URI.
        if (_listeners.Contains(store.Name)) return;

        // GH-5840: never start listening through the store of a tenant that was deregistered
        if (isRemoved(store)) return;

        var listener = new PostgresqlQueueListener(_queue, _runtime, _receiver, store.NpgsqlDataSource, store.Name);
        _listeners = _listeners.AddOrUpdate(store.Name, listener);
        _listenerStores = _listenerStores.AddOrUpdate(store.Name, store);
        await listener.StartAsync();

        // GH-5840: the tenant was deregistered while this listener was starting
        if (isRemoved(store))
        {
            await stopListenerAsync(store.Name, listener);
            return;
        }

        _logger.LogInformation("Started message listening for Postgresql queue {QueueName} on database {Database}", _queue.Name, store.Name);
    }

    // GH-5840. Store instances whose tenant was deregistered. The refresh loop above can be holding one from
    // before the removal, so starting a listener checks this. By reference: a readmitted tenant is a new store
    // with the same name, and that one is listened to again.
    private readonly HashSet<object> _removedStores = new(ReferenceEqualityComparer.Instance);

    // GH-5840. The store each database's listener was started through, so a removed tenant's store hands its
    // database's listener over only when it is the one that started it
    private ImHashMap<string, IMessageStore> _listenerStores = ImHashMap<string, IMessageStore>.Empty;

    private bool isRemoved(IMessageStore store)
    {
        lock (_removedStores) return _removedStores.Contains(store);
    }

    async ValueTask<bool> ITenantDatabaseListener.StopListeningToDatabaseAsync(IMessageStore store, IMessageStore? replacement)
    {
        if (store is not PostgresqlMessageStore) return false;

        lock (_removedStores) _removedStores.Add(store);

        if (!_listeners.TryFind(store.Name, out var listener)) return false;

        // Another store still covers this database: leave a listener some other store started alone
        if (replacement != null && (!_listenerStores.TryFind(store.Name, out var startedBy) || !ReferenceEquals(startedBy, store)))
        {
            return false;
        }

        await stopListenerAsync(store.Name, listener);
        _logger.LogInformation("Stopped message listening for queue {QueueName} on database {Database} of a removed tenant", _queue.Name, store.Name);

        if (replacement is PostgresqlMessageStore other)
        {
            await startListening(other);
        }

        return true;
    }

    private async Task stopListenerAsync(string databaseName, IListener listener)
    {
        if (_listeners.TryFind(databaseName, out var current) && ReferenceEquals(current, listener))
        {
            _listeners = _listeners.Remove(databaseName);
            _listenerStores = _listenerStores.Remove(databaseName);
        }

        await listener.StopAsync();
        await listener.DisposeAsync();
    }

    public IHandlerPipeline? Pipeline => _receiver.Pipeline;

    ValueTask IChannelCallback.CompleteAsync(Envelope envelope)
    {
        return new ValueTask();
    }

    ValueTask IChannelCallback.DeferAsync(Envelope envelope)
    {
        return new ValueTask();
    }

    public async ValueTask DisposeAsync()
    {
        await _cancellation.CancelAsync();
        _activator?.SafeDispose();
        foreach (var entry in _listeners.Enumerate())
        {
            await entry.Value.DisposeAsync();
        }
    }

    public Uri Address { get; set; }

    public async ValueTask StopAsync()
    {
        await _cancellation.CancelAsync();
        foreach (var entry in _listeners.Enumerate())
        {
            await entry.Value.StopAsync();
        }
    }

    public bool IsListeningToDatabase(string databaseName)
    {
        return _listeners.Contains(databaseName);
    }
}