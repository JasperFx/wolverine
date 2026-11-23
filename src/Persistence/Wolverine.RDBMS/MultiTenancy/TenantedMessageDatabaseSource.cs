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
internal abstract class TenantedMessageDatabaseSource<TDatabase> : ITenantedMessageSource, ITenantStoreRemoval
{
    private readonly List<Func<IMessageDatabase, ValueTask>> _configurations = new();
    private readonly object _locker = new();
    private ImHashMap<string, IMessageStore> _databases = ImHashMap<string, IMessageStore>.Empty;
    private ImHashMap<string, IMessageStore> _stores = ImHashMap<string, IMessageStore>.Empty;

    // GH-5840. Identifiers of databases whose last tenant was deregistered from this process. The store's own
    // tenancy (Marten's master table, say) keeps listing them, so refresh has to skip them. Only a live tenant
    // resolving to the database again takes it off.
    private ImHashMap<string, bool> _removedDatabases = ImHashMap<string, bool>.Empty;

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
        // GH-5840: a deregistered tenant is refused, so a stray message cannot rebuild its store
        Runtime.Options.RemovedTenants.AssertNotRemoved(tenantId);

        if (_stores.TryFind(tenantId, out var store))
        {
            return store;
        }

        var database = await findDatabaseAsync(tenantId);
        var identifier = identifierFor(database);

        if (_removedDatabases.Contains(identifier))
        {
            // A live tenant of a database whose other tenants were all removed: the database is in use after all
            lock (_locker)
            {
                _removedDatabases = _removedDatabases.Remove(identifier);
            }
        }

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
            if (_databases.Contains(identifier) || _removedDatabases.Contains(identifier)) continue;

            var wolverineStore = buildMessageStore(database);

            // GH-5840: a database every one of whose tenants was deregistered here, but which this process never
            // had a store for, so RemoveTenant could not name it
            if (wolverineStore.TenantIds.Count > 0 && wolverineStore.TenantIds.All(Runtime.Options.RemovedTenants.Contains))
            {
                lock (_locker)
                {
                    _removedDatabases = _removedDatabases.AddOrUpdate(identifier, true);
                }

                continue;
            }

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
    ///     GH-5840. Forget a deregistered tenant. The database's store goes only when no live tenant uses it any
    ///     more -- the shared-database guard, since one database can hold several tenants -- and then refresh
    ///     stops re-adding it. Nothing is disposed here: the caller stops the agent and listeners first.
    /// </summary>
    public TenantStoreRemoval RemoveTenant(string tenantId)
    {
        lock (_locker)
        {
            // A tenant resolved through FindAsync is in _stores. One that only arrived through RefreshAsync --
            // which is how the durability agent finds a tenant database -- is only in _databases, under the
            // database identifier, which for master-table tenancy is the tenant id itself.
            if (!_stores.TryFind(tenantId, out var store) && !_databases.TryFind(tenantId, out store))
            {
                store = _databases.Enumerate().Select(x => x.Value)
                    .FirstOrDefault(x => x.TenantIds.Contains(tenantId));
            }

            _stores = _stores.Remove(tenantId);

            if (store == null) return TenantStoreRemoval.Nothing;

            // Still serving another tenant: another tenant id mapped to this store, or another live tenant listed in
            // its database. Only this tenant's mapping goes.
            var removed = Runtime.Options.RemovedTenants;
            if (_stores.Enumerate().Any(x => ReferenceEquals(x.Value, store))
                || store.TenantIds.Any(x => x != tenantId && !removed.Contains(x)))
            {
                return new TenantStoreRemoval([], [], [store]);
            }

            foreach (var entry in _databases.Enumerate().Where(x => ReferenceEquals(x.Value, store)).ToArray())
            {
                _databases = _databases.Remove(entry.Key);
                _removedDatabases = _removedDatabases.AddOrUpdate(entry.Key, true);
            }

            // Another store over the same database: Marten's master table gives two tenants with one connection
            // string a store each. This one goes; the database's agent and listener carry on through the other.
            var replacement = _databases.Enumerate().Select(x => x.Value).FirstOrDefault(x => x.Uri == store.Uri);
            return replacement == null
                ? new TenantStoreRemoval([store], [], [])
                : new TenantStoreRemoval([], [new DetachedStore(store, replacement)], []);
        }
    }

    public async ValueTask ConfigureDatabaseAsync(Func<IMessageDatabase, ValueTask> configureDatabase)
    {
        foreach (var database in AllActive().OfType<IMessageDatabase>()) await configureDatabase(database);

        _configurations.Add(configureDatabase);
    }
}
