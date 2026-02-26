using ImTools;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Wolverine.Runtime;
using Wolverine.Transports;
using MultiTenantedMessageStore = Wolverine.Persistence.Durability.MultiTenantedMessageStore;

namespace Wolverine.Sqlite.Transport;

public class MultiTenantedQueueListener : IListener
{
    private readonly ILogger _logger;
    private readonly SqliteQueue _queue;
    private readonly MultiTenantedMessageStore _stores;
    private readonly IWolverineRuntime _runtime;
    private readonly IReceiver _receiver;
    private readonly CancellationTokenSource _cancellation;

    private ImHashMap<string, SqliteQueueListener> _listeners = ImHashMap<string, SqliteQueueListener>.Empty;
    private Task? _activator;

    public MultiTenantedQueueListener(ILogger logger, SqliteQueue queue, MultiTenantedMessageStore stores,
        IWolverineRuntime runtime, IReceiver receiver)
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
        await _stores.Source.RefreshAsync();

        if (_queue.Parent.Store != null)
        {
            await startListening(_queue.Parent.Store);
        }

        foreach (var store in _stores.Source.AllActive().OfType<SqliteMessageStore>())
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
                foreach (var store in databases.OfType<SqliteMessageStore>())
                {
                    if (!_listeners.Contains(store.Name))
                    {
                        await startListening(store);
                    }
                }
            }
        }, _cancellation.Token);
    }

    private async Task startListening(SqliteMessageStore store)
    {
        var listener = new SqliteQueueListener(_queue, _runtime, _receiver, store.DataSource, store.Name);
        _listeners = _listeners.AddOrUpdate(store.Name, listener);
        await listener.StartAsync();

        _logger.LogInformation("Started message listening for SQLite queue {QueueName} on database {Database}",
            _queue.Name, store.Name);
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
        _cancellation.Cancel();
        _activator?.SafeDispose();
        foreach (var entry in _listeners.Enumerate())
        {
            await entry.Value.DisposeAsync();
        }
    }

    public Uri Address { get; set; }

    public async ValueTask StopAsync()
    {
        _cancellation.Cancel();
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
