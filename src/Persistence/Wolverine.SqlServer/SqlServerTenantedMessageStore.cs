using ImTools;
using JasperFx.Core.Descriptors;
using JasperFx.MultiTenancy;
using Microsoft.Extensions.Logging;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;
using Wolverine.RDBMS.MultiTenancy;
using Wolverine.RDBMS.Sagas;
using Wolverine.Runtime;
using Wolverine.SqlServer.Persistence;

namespace Wolverine.SqlServer;

internal class SqlServerTenantedMessageStore : ITenantedMessageSource, IMessageDatabaseSource
{
    private ImHashMap<string, SqlServerMessageStore> _values = ImHashMap<string, SqlServerMessageStore>.Empty;
    private readonly SqlServerBackedPersistence _persistence;
    private readonly SagaTableDefinition[] _sagaTables;
    private readonly IWolverineRuntime _runtime;
    private ImHashMap<string, SqlServerMessageStore> _stores = ImHashMap<string, SqlServerMessageStore>.Empty;
    
    public SqlServerTenantedMessageStore(IWolverineRuntime runtime, SqlServerBackedPersistence persistence,
        SagaTableDefinition[] sagaTables)
    {
        if (persistence.ConnectionStringTenancy == null)
        {
            throw new ArgumentOutOfRangeException(nameof(persistence), "No multi-tenancy configured for Sql Server");
        }
        
        _persistence = persistence;
        _sagaTables = sagaTables;
        _runtime = runtime;
    }

    public ITenantedSource<string> DataSource { get; set; }

    public DatabaseCardinality Cardinality => DataSource.Cardinality;
    public async ValueTask<IMessageStore> FindAsync(string tenantId)
    {
        if (_stores.TryFind(tenantId, out var store))
        {
            return store;
        }

        var connectionString = await _persistence.ConnectionStringTenancy!.FindAsync(tenantId);
        store = buildStoreForConnectionString(connectionString);
        
        _stores = _stores.AddOrUpdate(tenantId, store);
        return store;
    }

    private SqlServerMessageStore buildStoreForConnectionString(string connectionString)
    {
        SqlServerMessageStore store;
        
        // TODO -- do some idempotency so that you don't build two or more stores for the same tenant id
        var settings = new DatabaseSettings
        {
            // Always disable command queues for tenant databases
            CommandQueuesEnabled = false,
            // TODO -- set the AutoCreate here
            ConnectionString = connectionString,
            IsMain = false,
            ScheduledJobLockId = _persistence.ScheduledJobLockId,
            SchemaName = _persistence.EnvelopeStorageSchemaName
        };

        store = new SqlServerMessageStore(settings, _runtime.Options.Durability,
            _runtime.LoggerFactory.CreateLogger<SqlServerMessageStore>(), _sagaTables);
        return store;
    }

    public async Task RefreshAsync()
    {
        await _persistence.ConnectionStringTenancy.RefreshAsync();

        foreach (var assignment in _persistence.ConnectionStringTenancy.AllActiveByTenant())
        {
            // TODO -- some idempotency
            if (!_stores.Contains(assignment.TenantId))
            {
                var store = buildStoreForConnectionString(assignment.Value);
                _stores = _stores.AddOrUpdate(assignment.TenantId, store);
            }
        }
    }

    public IReadOnlyList<IMessageStore> AllActive()
    {
        return _stores.Enumerate().Select(x => x.Value).ToList();
    }

    public IReadOnlyList<Assignment<IMessageStore>> AllActiveByTenant()
    {
        return _stores.Enumerate().Select(x => new Assignment<IMessageStore>(x.Key, x.Value)).ToList();
    }

    public async ValueTask ConfigureDatabaseAsync(Func<IMessageDatabase, ValueTask> configureDatabase)
    {
        await RefreshAsync();
        foreach (var store in _stores.Enumerate().Select(x => x.Value).ToArray())
        {
            await configureDatabase(store);
        }
    }
}