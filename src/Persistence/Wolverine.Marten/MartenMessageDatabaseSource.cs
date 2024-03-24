using JasperFx.Core;
using JasperFx.Core.Reflection;
using Marten;
using Marten.Storage;
using Microsoft.Extensions.Logging;
using Npgsql;
using Weasel.Core;
using Weasel.Postgresql;
using Wolverine.Postgresql;
using Wolverine.RDBMS;
using Wolverine.RDBMS.MultiTenancy;
using Wolverine.Runtime;

namespace Wolverine.Marten;

internal class MartenMessageDatabaseSource : IMessageDatabaseSource
{
    private readonly string _schemaName;
    private readonly IDocumentStore _store;
    private readonly IWolverineRuntime _runtime;
    private ImHashMap<string, PostgresqlMessageStore> _stores = ImHashMap<string, PostgresqlMessageStore>.Empty;
    private ImHashMap<string, PostgresqlMessageStore> _databases = ImHashMap<string, PostgresqlMessageStore>.Empty;
    private readonly object _locker = new();

    public MartenMessageDatabaseSource(string schemaName, IDocumentStore store, IWolverineRuntime runtime)
    {
        _schemaName = schemaName;
        _store = store;
        _runtime = runtime;
    }
    
    public async ValueTask<IMessageDatabase> FindDatabaseAsync(string tenantId)
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
        
        if (_store.Options.As<StoreOptions>().AutoCreateSchemaObjects != AutoCreate.None)
        {
            // TODO -- add some resiliency here
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
            CommandQueuesEnabled = false,
            DataSource = database.As<PostgresqlDatabase>().DataSource
        };

        var store = new PostgresqlMessageStore(settings, _runtime.Options.Durability, database.As<PostgresqlDatabase>().DataSource,
            _runtime.LoggerFactory.CreateLogger<PostgresqlMessageStore>())
        {
            Name = new NpgsqlConnectionStringBuilder(settings.ConnectionString).Database ?? database.Identifier
        };

        return store;
    }

    public async Task RefreshAsync()
    {
        var martenDatabases = await _store.Storage.AllDatabases();
        foreach (var martenDatabase in martenDatabases)
        {
            var wolverineStore = createWolverineStore(martenDatabase);

            if (martenDatabase.AutoCreate != AutoCreate.None)
            {
                await wolverineStore.MigrateAsync();
            }
            
            _databases = _databases.AddOrUpdate(martenDatabase.Identifier, wolverineStore);
        }
    }

    public IReadOnlyList<IMessageDatabase> AllActive()
    {
        return _databases.Enumerate().Select(x => x.Value).ToList();
    }
}