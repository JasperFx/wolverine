using ImTools;
using JasperFx;
using JasperFx.Core.Reflection;
using JasperFx.Descriptors;
using JasperFx.MultiTenancy;
using Marten;
using Marten.Storage;
using Microsoft.Extensions.Logging;
using Npgsql;
using Weasel.Postgresql;
using Wolverine.Persistence.Durability;
using Wolverine.Postgresql;
using Wolverine.RDBMS;
using Wolverine.RDBMS.MultiTenancy;
using Wolverine.Runtime;

namespace Wolverine.Marten;

/// <summary>
///     Built to support separate stores in Marten
/// </summary>
/// <typeparam name="T"></typeparam>
internal class MartenMessageDatabaseSource<T> : MartenMessageDatabaseSource where T : IDocumentStore
{
    public MartenMessageDatabaseSource(string schemaName, AutoCreate autoCreate, T store, IWolverineRuntime runtime) :
        base(schemaName, autoCreate, store, runtime)
    {
    }
}

internal class MartenMessageDatabaseSource : ITenantedMessageSource, ITenantStoreRemoval
{
    private readonly AutoCreate _autoCreate;

    private readonly List<Func<IMessageDatabase, ValueTask>> _configurations = new();
    private readonly object _locker = new();
    private readonly IWolverineRuntime _runtime;
    private readonly string _schemaName;
    private readonly IDocumentStore _store;
    private ImHashMap<string, IMessageStore> _databases = ImHashMap<string, IMessageStore>.Empty;
    private ImHashMap<string, IMessageStore> _stores = ImHashMap<string, IMessageStore>.Empty;

    // GH-5840. Identifiers of databases whose last tenant was deregistered from this process. Marten's tenancy keeps
    // listing them, so refresh skips them. Only a live tenant resolving to the database again takes it off.
    private ImHashMap<string, bool> _removedDatabases = ImHashMap<string, bool>.Empty;

    public MartenMessageDatabaseSource(
        string schemaName,
        AutoCreate autoCreate,
        IDocumentStore store,
        IWolverineRuntime runtime)
    {
        _schemaName = schemaName;
        _autoCreate = autoCreate;
        _store = store;
        _runtime = runtime;
    }

    public DatabaseCardinality Cardinality => _store.Options.Tenancy.Cardinality;

    public async ValueTask<IMessageStore> FindAsync(string tenantId)
    {
        // GH-5840: a deregistered tenant is refused, so a stray message cannot rebuild its store
        _runtime.Options.RemovedTenants.AssertNotRemoved(tenantId);

        if (_stores.TryFind(tenantId, out var store))
        {
            return store;
        }

        // Remember, Marten makes it legal to store multiple tenants in one database
        // so it's not 1 to 1 on tenant to database
        var database = await _store.Storage.FindOrCreateDatabase(tenantId);

        if (_removedDatabases.Contains(database.Identifier))
        {
            // A live tenant of a database whose other tenants were all removed: the database is in use after all
            lock (_locker)
            {
                _removedDatabases = _removedDatabases.Remove(database.Identifier);
            }
        }

        if (_databases.TryFind(database.Identifier, out store))
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

            if (_databases.TryFind(database.Identifier, out store))
            {
                _stores = _stores.AddOrUpdate(tenantId, store);
                return store;
            }

            store = createTenantWolverineStore(database);
            store.Initialize(_runtime);

            _stores = _stores.AddOrUpdate(tenantId, store);
            _databases = _databases.AddOrUpdate(database.Identifier, store);
        }

        foreach (var configuration in _configurations) await configuration((IMessageDatabase)store);

        // TODO -- add some resiliency here
        if (_autoCreate != AutoCreate.None)
        {
            await store.Admin.MigrateAsync();
        }
        else
        {
            _runtime.LoggerFactory.CreateLogger<MartenMessageDatabaseSource>().LogInformation(
                "Skipping message storage migration for tenant message database {Database} because AutoCreate is None",
                database.Identifier);
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
        var martenDatabases = await _store.Storage.AllDatabases();
        foreach (var martenDatabase in martenDatabases)
        {
            if (!_databases.Contains(martenDatabase.Identifier) && !_removedDatabases.Contains(martenDatabase.Identifier))
            {
                var wolverineStore = createTenantWolverineStore(martenDatabase);

                // GH-5840: a database every one of whose tenants was deregistered here, but which this process
                // never had a store for, so RemoveTenant could not name it
                if (wolverineStore.TenantIds.Count > 0 && wolverineStore.TenantIds.All(_runtime.Options.RemovedTenants.Contains))
                {
                    lock (_locker)
                    {
                        _removedDatabases = _removedDatabases.AddOrUpdate(martenDatabase.Identifier, true);
                    }

                    continue;
                }

                if (withMigration && _runtime.Options.AutoBuildMessageStorageOnStartup != AutoCreate.None)
                {
                    await wolverineStore.Admin.MigrateAsync();
                }
                
                _databases = _databases.AddOrUpdate(martenDatabase.Identifier, wolverineStore);
            }
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
    ///     GH-5840. Forget a deregistered tenant. Nothing is disposed here: the caller stops the agent and listeners first.
    /// </summary>
    public TenantStoreRemoval RemoveTenant(string tenantId)
    {
        lock (_locker)
        {
            return DatabaseCacheRemoval.RemoveTenant(ref _stores, ref _databases, ref _removedDatabases, tenantId,
                _runtime.Options.RemovedTenants);
        }
    }

    public async ValueTask ConfigureDatabaseAsync(Func<IMessageDatabase, ValueTask> configureDatabase)
    {
        foreach (var database in AllActive().OfType<IMessageDatabase>()) await configureDatabase(database);

        _configurations.Add(configureDatabase);
    }

    private PostgresqlMessageStore createTenantWolverineStore(IMartenDatabase database)
    {
        var settings = new DatabaseSettings
        {
            SchemaName = _schemaName,
            Role = MessageStoreRole.Tenant,
            AutoCreate = _autoCreate,
            CommandQueuesEnabled = false,
            DataSource = database.As<PostgresqlDatabase>().DataSource
        };

        // The tenant database's data source is Marten's, so Marten disposes it when it should
        var store = new PostgresqlMessageStore(settings, _runtime.Options.Durability,
            database.As<PostgresqlDatabase>().DataSource,
            _runtime.LoggerFactory.CreateLogger<PostgresqlMessageStore>(), ownsDataSource: false)
        {
            Name = database.Identifier ?? new NpgsqlConnectionStringBuilder(settings.ConnectionString).Database!
        };
        
        store.TenantIds.AddRange(database.TenantIds.Distinct());

        return store;
    }
}