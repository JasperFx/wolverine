using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Wolverine.RDBMS.MultiTenancy;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.Postgresql.Transport;

public class MultiTenantedQueueListener : IListener
{
    private readonly ILogger _logger;
    private readonly PostgresqlQueue _queue;
    private readonly MultiTenantedMessageDatabase _databases;
    private readonly IWolverineRuntime _runtime;
    private readonly IReceiver _receiver;
    private readonly CancellationTokenSource _cancellation;

    private ImHashMap<string, PostgresqlQueueListener> _listeners = ImHashMap<string, PostgresqlQueueListener>.Empty;
    private Task? _activator;

    public MultiTenantedQueueListener(ILogger logger, PostgresqlQueue queue, MultiTenantedMessageDatabase databases, IWolverineRuntime runtime, IReceiver receiver)
    {
        _logger = logger;
        _queue = queue;
        _databases = databases;
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

        foreach (var store in _databases.AllDatabases().OfType<PostgresqlMessageStore>())
        {
            await startListening(store);
        }

        _activator = Task.Run(async () =>
        {
            while (!_cancellation.IsCancellationRequested)
            {
                await Task.Delay(_runtime.Options.Durability.TenantCheckPeriod, _cancellation.Token);

                var databases = await _databases.CheckForDatabasesAsync(_runtime);
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
        var listener = new PostgresqlQueueListener(_queue, _runtime, _receiver, store.DataSource, store.Name);
        _listeners = _listeners.AddOrUpdate(store.Name, listener);
        await listener.StartAsync();
        
        _logger.LogInformation("Started message listening for Postgresql queue {QueueName} on database {Database}", _queue.Name, store.Name);
    }

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