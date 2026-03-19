// NOTE: This file requires Polecat 1.1+ (public ITenancy, ConnectionFactory, PolecatDatabase.ConnectionString)
// Uncomment #if POLECAT_1_1 / #endif when ready, or remove the guards after upgrading the Polecat NuGet
#if POLECAT_1_1
using ImTools;
using JasperFx;
using JasperFx.Core.Reflection;
using JasperFx.Descriptors;
using JasperFx.MultiTenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Polecat;
using Polecat.Storage;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;
using Wolverine.RDBMS.Sagas;
using Wolverine.Runtime;
using Wolverine.SqlServer.Persistence;

namespace Wolverine.Polecat;

/// <summary>
///     Built to support separate stores in Polecat for multi-tenancy scenarios
/// </summary>
/// <typeparam name="T"></typeparam>
internal class PolecatMessageDatabaseSource<T> : PolecatMessageDatabaseSource where T : IDocumentStore
{
    public PolecatMessageDatabaseSource(string schemaName, AutoCreate autoCreate, T store, IWolverineRuntime runtime) :
        base(schemaName, autoCreate, store, runtime)
    {
    }
}

internal class PolecatMessageDatabaseSource : ITenantedMessageSource
{
    private readonly AutoCreate _autoCreate;
    private readonly List<Func<IMessageDatabase, ValueTask>> _configurations = new();
    private readonly object _locker = new();
    private readonly IWolverineRuntime _runtime;
    private readonly string _schemaName;
    private readonly IDocumentStore _store;
    private readonly ITenancy _tenancy;
    private ImHashMap<string, IMessageStore> _databases = ImHashMap<string, IMessageStore>.Empty;
    private ImHashMap<string, IMessageStore> _stores = ImHashMap<string, IMessageStore>.Empty;

    public PolecatMessageDatabaseSource(
        string schemaName,
        AutoCreate autoCreate,
        IDocumentStore store,
        IWolverineRuntime runtime)
    {
        _schemaName = schemaName;
        _autoCreate = autoCreate;
        _store = store;
        _runtime = runtime;

        // Requires Polecat 1.1+ where StoreOptions.Tenancy is public
        _tenancy = store.Options.Tenancy
                   ?? throw new InvalidOperationException(
                       "Polecat store does not have tenancy configured. Use MultiTenantedDatabases() in store setup.");
    }

    public DatabaseCardinality Cardinality => _tenancy.Cardinality;

    public async ValueTask<IMessageStore> FindAsync(string tenantId)
    {
        if (_stores.TryFind(tenantId, out var store))
        {
            return store;
        }

        var factory = _tenancy.GetConnectionFactory(tenantId);
        var database = _tenancy.GetDatabase(tenantId);
        var identifier = database.Identifier;

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
            if (_stores.TryFind(tenantId, out store))
            {
                return store;
            }

            if (_databases.TryFind(identifier, out store))
            {
                _stores = _stores.AddOrUpdate(tenantId, store);
                return store;
            }

            store = createTenantWolverineStore(factory.ConnectionString, identifier);
            store.Initialize(_runtime);

            _stores = _stores.AddOrUpdate(tenantId, store);
            _databases = _databases.AddOrUpdate(identifier, store);
        }

        foreach (var configuration in _configurations) await configuration((IMessageDatabase)store);

        if (_autoCreate != AutoCreate.None)
        {
            await store.Admin.MigrateAsync();
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
        var polecatDatabases = _tenancy.AllDatabases();
        foreach (var polecatDatabase in polecatDatabases)
        {
            if (!_databases.Contains(polecatDatabase.Identifier))
            {
                var connectionString = polecatDatabase.ConnectionString ?? _store.Options.ConnectionString!;

                var wolverineStore = createTenantWolverineStore(connectionString, polecatDatabase.Identifier);
                if (withMigration && _runtime.Options.AutoBuildMessageStorageOnStartup != AutoCreate.None)
                {
                    await wolverineStore.Admin.MigrateAsync();
                }

                _databases = _databases.AddOrUpdate(polecatDatabase.Identifier, wolverineStore);
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

    public async ValueTask ConfigureDatabaseAsync(Func<IMessageDatabase, ValueTask> configureDatabase)
    {
        foreach (var database in AllActive().OfType<IMessageDatabase>()) await configureDatabase(database);

        _configurations.Add(configureDatabase);
    }

    private SqlServerMessageStore createTenantWolverineStore(string connectionString, string identifier)
    {
        var settings = new DatabaseSettings
        {
            SchemaName = _schemaName,
            Role = MessageStoreRole.Tenant,
            AutoCreate = _autoCreate,
            CommandQueuesEnabled = false,
            ConnectionString = connectionString
        };

        var sagaTypes = _runtime.Services.GetServices<SagaTableDefinition>();
        var store = new SqlServerMessageStore(settings, _runtime.Options.Durability,
            _runtime.LoggerFactory.CreateLogger<SqlServerMessageStore>(), sagaTypes)
        {
            Name = identifier
        };

        return store;
    }
}
#endif
