using ImTools;
using JasperFx.Core;
using JasperFx.Descriptors;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;

namespace Wolverine.Marten.Distribution;

internal class EventStoreAgents : IAsyncDisposable
{
    private readonly IEventStore _store;
    private readonly IObserver<ShardState>[] _observers;
    private readonly SemaphoreSlim _daemonLock = new(1);
    private ImHashMap<DatabaseId, IProjectionDaemon> _daemons = ImHashMap<DatabaseId, IProjectionDaemon>.Empty;
    private readonly List<ShardName> _shardNames = new();
    
    public EventStoreAgents(IEventStore store, IObserver<ShardState>[] observers)
    {
        _store = store;
        _observers = observers ?? [];
        Identity = _store.Identity;
    }
    
    public EventStoreIdentity Identity { get; }

    public async ValueTask DisposeAsync()
    {
        foreach (var entry in _daemons.Enumerate())
        {
            try
            {
                await entry.Value.StopAllAsync();
                entry.Value.SafeDispose();
            }
            catch (Exception e)
            {
                // TODO -- probably want to log this just in case
            }
        }
    }

    public async ValueTask<IProjectionDaemon> FindDaemonAsync(DatabaseId databaseId)
    {
        if (_daemons.TryFind(databaseId, out var daemon))
        {
            return daemon;
        }

        await _daemonLock.WaitAsync();
        try
        {
            // Gotta do the double lock thing
            if (_daemons.TryFind(databaseId, out daemon))
            {
                return daemon;
            }

            daemon = await _store.BuildProjectionDaemonAsync(databaseId);
            foreach (var observer in _observers)
            {
                // TODO -- do we need to care about un-subscribing?
                daemon.Tracker.Subscribe(observer);
            }
            
            _daemons = _daemons.AddOrUpdate(databaseId, daemon);
        }
        finally
        {
            _daemonLock.Release();
        }

        return daemon;
    }

    public async ValueTask<IReadOnlyList<Uri>> SupportedAgentsAsync(CancellationToken cancellation)
    {
        var list = new List<Uri>();
        var usage = await _store.TryCreateUsage(cancellation);
        if (usage == null) return list;

        // Using this to keep from double dipping
        var databaseIds = new List<DatabaseId>();
        foreach (var database in usage.Database.Databases)
        {
            var id = new DatabaseId(database.ServerName, database.DatabaseName);
            databaseIds.Add(id);
            
            foreach (var shardName in usage.Subscriptions.Where(x => x.Lifecycle == ProjectionLifecycle.Async).SelectMany(x => x.ShardNames))
            {
                _shardNames.Fill(shardName);
                var uri = EventSubscriptionAgentFamily.UriFor(_store.Identity, id, shardName);
                list.Add(uri);
            }
        }

        if (usage.Database.MainDatabase != null)
        {
            var database = usage.Database.MainDatabase;
            var id = new DatabaseId(database.ServerName, database.DatabaseName);
            if (!databaseIds.Contains(id))
            {
                foreach (var shardName in usage.Subscriptions.Where(x => x.Lifecycle == ProjectionLifecycle.Async).SelectMany(x => x.ShardNames))
                {
                    _shardNames.Fill(shardName);
                    var uri = EventSubscriptionAgentFamily.UriFor(_store.Identity, id, shardName);
                    list.Add(uri);
                }
            }
        }

        return list;
    }

    public async Task<EventSubscriptionAgent> BuildAgentAsync(Uri uri, DatabaseId databaseId, string shardPath)
    {
        var shardName = _shardNames.FirstOrDefault(x => x.RelativeUrl == shardPath);
        if (shardName == null)
        {
            throw new ArgumentOutOfRangeException(nameof(shardPath), $"Unable to find a shard with path '{shardPath}'");
        }

        var daemon = await FindDaemonAsync(databaseId);
        
        return new EventSubscriptionAgent(uri, shardName, daemon);
    }

    public async Task StartAllAsync(CancellationToken cancellationToken)
    {
        var usage = await _store.TryCreateUsage(cancellationToken);
        if (usage == null)
        {
            return;
        }

        foreach (var database in usage.Database.Databases)
        {
            var id = new DatabaseId(database.ServerName, database.DatabaseName);
            var daemon = await FindDaemonAsync(id);

            await daemon.StartAllAsync();
        }

        if (usage.Database.MainDatabase != null)
        {
            var id = new DatabaseId(usage.Database.MainDatabase.ServerName, usage.Database.MainDatabase.DatabaseName);
            var daemon = await FindDaemonAsync(id);

            await daemon.StartAllAsync();
        }
    }

    public async Task StopAllAsync(CancellationToken cancellationToken)
    {
        foreach (var kvEntry in _daemons.Enumerate())
        {
            var daemon = kvEntry.Value;
            await daemon.StopAllAsync();
        }
    }
    
    public async ValueTask<IReadOnlyList<IProjectionDaemon>> AllDaemonsAsync()
    {
        var usage = await _store.TryCreateUsage(CancellationToken.None);
        if (usage == null)
        {
            return [];
        }

        var list = new List<IProjectionDaemon>();
        
        foreach (var database in usage.Database.Databases)
        {
            var id = new DatabaseId(database.ServerName, database.DatabaseName);
            var daemon = await FindDaemonAsync(id);

            list.Add(daemon);
        }

        if (usage.Database.MainDatabase != null && usage.Database.Cardinality == DatabaseCardinality.Single)
        {
            var id = new DatabaseId(usage.Database.MainDatabase.ServerName, usage.Database.MainDatabase.DatabaseName);
            var daemon = await FindDaemonAsync(id);

            list.Add(daemon);
        }

        return list;
    }

    public IProjectionDaemon DaemonForMainDatabase()
    {
        throw new NotSupportedException("This method is not supported with the Wolverine managed projection/subscription distribution");
    }

    public ValueTask<IProjectionDaemon> DaemonForDatabase(string databaseIdentifier)
    {
        throw new NotSupportedException("This method is not supported with the Wolverine managed projection/subscription distribution");
    }


}