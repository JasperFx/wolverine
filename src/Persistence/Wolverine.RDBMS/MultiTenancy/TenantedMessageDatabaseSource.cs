using ImTools;
using JasperFx;
using JasperFx.Descriptors;
using JasperFx.MultiTenancy;
using Microsoft.Extensions.Logging;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;

namespace Wolverine.RDBMS.MultiTenancy;

/// <summary>
///     GH-5145: the per-tenant message stores of a document store integration (Marten, Polecat) that keeps its
///     tenants in more than one database. The caching, locking, migration and per-database configuration live
///     here once; a store supplies how a tenant id maps to one of its databases and how to build a Wolverine
///     message store over that database.
/// </summary>
/// <remarks>
///     A store can put several tenants in one database, so tenant ids and databases are not one to one: a
///     tenant whose database already has a message store reuses it. The tenant-to-connection step stays with
///     the store because JasperFx's <c>IEventDatabase</c> carries no connection or data source today.
/// </remarks>
/// <typeparam name="TDatabase">The store's own handle for one of its databases</typeparam>
internal abstract class TenantedMessageDatabaseSource<TDatabase> : ITenantedMessageSource
{
    private readonly List<Func<IMessageDatabase, ValueTask>> _configurations = new();
    private readonly object _locker = new();
    private ImHashMap<string, IMessageStore> _databases = ImHashMap<string, IMessageStore>.Empty;
    private ImHashMap<string, IMessageStore> _stores = ImHashMap<string, IMessageStore>.Empty;

    protected TenantedMessageDatabaseSource(AutoCreate autoCreate, IWolverineRuntime runtime)
    {
        AutoCreate = autoCreate;
        Runtime = runtime;
    }

    protected AutoCreate AutoCreate { get; }
    protected IWolverineRuntime Runtime { get; }

    public abstract DatabaseCardinality Cardinality { get; }

    /// <summary>
    ///     The store's database for this tenant, creating it if the store's tenancy does that
    /// </summary>
    protected abstract ValueTask<TDatabase> findDatabaseAsync(string tenantId);

    /// <summary>
    ///     Every database the store knows about right now
    /// </summary>
    protected abstract ValueTask<IReadOnlyList<TDatabase>> allDatabasesAsync();

    /// <summary>
    ///     The database's identifier, which keys the message store cache
    /// </summary>
    protected abstract string identifierFor(TDatabase database);

    /// <summary>
    ///     Build an uninitialized Wolverine message store over the database, with the tenant role
    /// </summary>
    protected abstract IMessageStore buildMessageStore(TDatabase database);

    public async ValueTask<IMessageStore> FindAsync(string tenantId)
    {
        if (_stores.TryFind(tenantId, out var store))
        {
            return store;
        }

        var database = await findDatabaseAsync(tenantId);
        var identifier = identifierFor(database);

        if (_databases.TryFind(identifier, out store))
        {
            lock (_locker)
            {
                if (!_stores.Contains(tenantId))
                {
                    _stores = _stores.AddOrUpdate(tenantId, store);
                }
            }

            return store;
        }

        lock (_locker)
        {
            // Try again to see if some other thread built it
            if (_stores.TryFind(tenantId, out store))
            {
                return store;
            }

            if (_databases.TryFind(identifier, out store))
            {
                _stores = _stores.AddOrUpdate(tenantId, store);
                return store;
            }

            store = buildMessageStore(database);
            store.Initialize(Runtime);

            _stores = _stores.AddOrUpdate(tenantId, store);
            _databases = _databases.AddOrUpdate(identifier, store);
        }

        foreach (var configuration in _configurations) await configuration((IMessageDatabase)store);

        if (AutoCreate != AutoCreate.None)
        {
            await store.Admin.MigrateAsync();
        }
        else
        {
            Runtime.LoggerFactory.CreateLogger(GetType()).LogInformation(
                "Skipping message storage migration for tenant message database {Database} because AutoCreate is None",
                identifier);
        }

        return store;
    }

    public Task RefreshAsync()
    {
        return RefreshAsync(true);
    }

    public Task RefreshLiteAsync()
    {
        return RefreshAsync(false);
    }

    public async Task RefreshAsync(bool withMigration)
    {
        var databases = await allDatabasesAsync();
        foreach (var database in databases)
        {
            var identifier = identifierFor(database);
            if (_databases.Contains(identifier)) continue;

            var wolverineStore = buildMessageStore(database);
            if (withMigration && Runtime.Options.AutoBuildMessageStorageOnStartup != AutoCreate.None)
            {
                await wolverineStore.Admin.MigrateAsync();
            }

            _databases = _databases.AddOrUpdate(identifier, wolverineStore);
        }
    }

    public IReadOnlyList<IMessageStore> AllActive()
    {
        return _databases.Enumerate().Select(x => x.Value).ToList();
    }

    public IReadOnlyList<Assignment<IMessageStore>> AllActiveByTenant()
    {
        return _databases.Enumerate().Select(x => new Assignment<IMessageStore>(x.Key, x.Value)).ToList();
    }

    /// <summary>
    ///     Evict and dispose the cached Wolverine message store for a given tenant.
    ///     Called when a tenant is disabled or removed from dynamic tenancy.
    /// </summary>
    public async ValueTask RemoveTenantStoreAsync(string tenantId)
    {
        IMessageStore? storeToDispose = null;

        lock (_locker)
        {
            if (_stores.TryFind(tenantId, out var store))
            {
                _stores = _stores.Remove(tenantId);

                // Also remove from _databases if this store is cached there
                foreach (var entry in _databases.Enumerate())
                {
                    if (ReferenceEquals(entry.Value, store))
                    {
                        _databases = _databases.Remove(entry.Key);
                        break;
                    }
                }

                storeToDispose = store;
            }
        }

        if (storeToDispose != null)
        {
            await storeToDispose.DisposeAsync();
        }
    }

    public async ValueTask ConfigureDatabaseAsync(Func<IMessageDatabase, ValueTask> configureDatabase)
    {
        foreach (var database in AllActive().OfType<IMessageDatabase>()) await configureDatabase(database);

        _configurations.Add(configureDatabase);
    }
}
