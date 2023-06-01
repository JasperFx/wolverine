using System.Collections.Immutable;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Marten;
using Marten.Storage;
using Microsoft.Extensions.Logging;
using Weasel.Core;
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
    private ImmutableArray<PostgresqlMessageStore> _all = ImmutableArray<PostgresqlMessageStore>.Empty;
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
        var existing = _all.FirstOrDefault(x => x.Name == database.Identifier);

        if (existing != null)
        {
            lock (_locker)
            {
                _stores = _stores.AddOrUpdate(tenantId, existing);
            }
            
            return existing;
        }

        lock (_locker)
        {
            // Try again to see if some other thread built it
            if (_stores.TryFind(tenantId, out store)) return store;

            store = createWolverineStore(database);

            _stores = _stores.AddOrUpdate(tenantId, store);
            _all = _all.Add(store);
        }
        
        if (_store.Options.As<StoreOptions>().AutoCreateSchemaObjects != AutoCreate.None)
        {
            await store.Admin.MigrateAsync();
        }

        // Got to do this to start the database batching
        await store.InitializeAsync(_runtime);
        
        return store;
    }

    private PostgresqlMessageStore createWolverineStore(IMartenDatabase database)
    {
        var settings = new DatabaseSettings
        {
            SchemaName = _schemaName,
            IsMaster = false,
            CommandQueuesEnabled = false,
            ConnectionString = database.CreateConnection().ConnectionString
        };

        return new PostgresqlMessageStore(settings, _runtime.Options.Durability,
            _runtime.LoggerFactory.CreateLogger<PostgresqlMessageStore>());
    }

    public async Task InitializeAsync()
    {
        var martenDatabases = await _store.Storage.AllDatabases();
        foreach (var martenDatabase in martenDatabases)
        {
            var wolverineStore = createWolverineStore(martenDatabase);
            _all = _all.Add(wolverineStore);
        }
    }

    public IReadOnlyList<IMessageDatabase> AllActive()
    {
        return _all;
    }
}