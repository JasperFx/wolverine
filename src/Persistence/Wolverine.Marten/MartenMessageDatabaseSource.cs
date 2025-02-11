using JasperFx;
using JasperFx.Core;
using JasperFx.Core.Descriptions;
using JasperFx.Core.Reflection;
using Marten;
using Marten.Storage;
using Microsoft.Extensions.Logging;
using Npgsql;
using Weasel.Core;
using Weasel.Core.Migrations;
using Weasel.Postgresql;
using Wolverine.Persistence.Durability;
using Wolverine.Postgresql;
using Wolverine.RDBMS;
using Wolverine.RDBMS.MultiTenancy;
using Wolverine.Runtime;

namespace Wolverine.Marten;

/// <summary>
/// Built to support separate stores in Marten
/// </summary>
/// <typeparam name="T"></typeparam>
internal class MartenMessageDatabaseSource<T> : MartenMessageDatabaseSource where T : IDocumentStore
{
    public MartenMessageDatabaseSource(string schemaName, AutoCreate autoCreate, T store, IWolverineRuntime runtime) : base(schemaName, autoCreate, store, runtime)
    {
    }
}

internal class MartenMessageDatabaseSource : IMessageDatabaseSource
{
    private readonly string _schemaName;
    private readonly AutoCreate _autoCreate;
    private readonly IDocumentStore _store;
    private readonly IWolverineRuntime _runtime;
    private ImHashMap<string, PostgresqlMessageStore> _stores = ImHashMap<string, PostgresqlMessageStore>.Empty;
    private ImHashMap<string, PostgresqlMessageStore> _databases = ImHashMap<string, PostgresqlMessageStore>.Empty;
    private readonly object _locker = new();

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

    public async ValueTask<IMessageStore> FindDatabaseAsync(string tenantId)
    {
        if (_stores.TryFind(tenantId, out var store)) return store;

        // Remember, Marten makes it legal to store multiple tenants in one database
        // so it's not 1 to 1 on tenant to database
        var database = await _store.Storage.FindOrCreateDatabase(tenantId);

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
            if (_stores.TryFind(tenantId, out store)) return store;

            if (_databases.TryFind(database.Identifier, out store))
            {
                _stores = _stores.AddOrUpdate(tenantId, store);
                return store;
            }

            store = createWolverineStore(database);
            store.Initialize(_runtime);

            _stores = _stores.AddOrUpdate(tenantId, store);
            _databases = _databases.AddOrUpdate(database.Identifier, store);
        }

        foreach (var configuration in _configurations)
        {
            await configuration(store);
        }

        // TODO -- add some resiliency here
        if (_autoCreate != AutoCreate.None)
        {
            await store.Admin.MigrateAsync();
        }

        return store;
    }

    private PostgresqlMessageStore createWolverineStore(IMartenDatabase database)
    {
        var settings = new DatabaseSettings
        {
            SchemaName = _schemaName,
            IsMaster = false,
            AutoCreate = _autoCreate,
            CommandQueuesEnabled = false,
            DataSource = database.As<PostgresqlDatabase>().DataSource
        };

        var store = new PostgresqlMessageStore(settings, _runtime.Options.Durability, database.As<PostgresqlDatabase>().DataSource,
            _runtime.LoggerFactory.CreateLogger<PostgresqlMessageStore>())
        {
            Descriptor = database.As<PostgresqlDatabase>().Describe(),
            Name = database.Identifier ?? new NpgsqlConnectionStringBuilder(settings.ConnectionString).Database
        };

        return store;
    }

    public async Task RefreshAsync()
    {
        var martenDatabases = await _store.Storage.AllDatabases();
        foreach (var martenDatabase in martenDatabases)
        {
            var wolverineStore = createWolverineStore(martenDatabase);
            if (_autoCreate != AutoCreate.None)
            {
                await wolverineStore.MigrateAsync();
            }

            _databases = _databases.AddOrUpdate(martenDatabase.Identifier, wolverineStore);
        }
    }

    public IReadOnlyList<IMessageStore> AllActive()
    {
        return _databases.Enumerate().Select(x => x.Value).ToList();
    }

    private readonly List<Func<IMessageDatabase, ValueTask>> _configurations = new();

    public async ValueTask ConfigureDatabaseAsync(Func<IMessageDatabase, ValueTask> configureDatabase)
    {
        foreach (var database in AllActive().OfType<IMessageDatabase>())
        {
            await configureDatabase(database);
        }

        _configurations.Add(configureDatabase);
    }
}