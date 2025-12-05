using ImTools;
using JasperFx;
using JasperFx.Core;
using JasperFx.Descriptors;
using JasperFx.MultiTenancy;
using Microsoft.Extensions.Logging;
using Npgsql;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;
using Wolverine.RDBMS.MultiTenancy;
using Wolverine.RDBMS.Sagas;
using Wolverine.Runtime;

namespace Wolverine.Postgresql;

internal class PostgresqlTenantedMessageStore : ITenantedMessageSource
{
    private readonly PostgresqlBackedPersistence _persistence;
    private readonly SagaTableDefinition[] _sagaTables;
    private readonly IWolverineRuntime _runtime;
    private ImHashMap<string, PostgresqlMessageStore> _stores = ImHashMap<string, PostgresqlMessageStore>.Empty;
    
    public PostgresqlTenantedMessageStore(IWolverineRuntime runtime, PostgresqlBackedPersistence persistence,
        SagaTableDefinition[] sagaTables)
    {
        _persistence = persistence;
        _sagaTables = sagaTables;
        _runtime = runtime;
    }

    public DatabaseCardinality Cardinality => _persistence.DataSourceTenancy?.Cardinality ??
                                              _persistence.ConnectionStringTenancy.Cardinality;
    public async ValueTask<IMessageStore> FindAsync(string tenantId)
    {
        if (_stores.TryFind(tenantId, out var store))
        {
            return store;
        }

        if (_persistence.DataSourceTenancy != null)
        {
            var source = await _persistence.DataSourceTenancy.FindAsync(tenantId);
            store = buildTenantStoreForDataSource(source);
        }
        else
        {
            var connectionString = await _persistence.ConnectionStringTenancy.FindAsync(tenantId);
            store = buildTenantStoreForConnectionString(connectionString);
        }
        
        store.TenantIds.Fill(tenantId);

        if (_runtime.Options.AutoBuildMessageStorageOnStartup != AutoCreate.None)
        {
            await store.Admin.MigrateAsync();
        }
        
        _stores = _stores.AddOrUpdate(tenantId, store);
        return store;
    }

    private PostgresqlMessageStore buildTenantStoreForConnectionString(string connectionString)
    {
        PostgresqlMessageStore store;
        // TODO -- do some idempotency so that you don't build two or more stores for the same tenant id
        var npgsqlDataSource = NpgsqlDataSource.Create(connectionString);
        var settings = new DatabaseSettings
        {
            // Always disable command queues for tenant databases
            CommandQueuesEnabled = false,
            // TODO -- set the AutoCreate here
            DataSource = npgsqlDataSource,
            ConnectionString = connectionString,
            Role = MessageStoreRole.Tenant,
            ScheduledJobLockId = _persistence.ScheduledJobLockId,
            SchemaName = _persistence.EnvelopeStorageSchemaName
        };

        store = new PostgresqlMessageStore(settings, _runtime.Options.Durability, npgsqlDataSource,
            _runtime.LoggerFactory.CreateLogger<PostgresqlMessageStore>(), _sagaTables);
        store.Name = connectionString;
        return store;
    }

    private PostgresqlMessageStore buildTenantStoreForDataSource(NpgsqlDataSource source)
    {
        PostgresqlMessageStore store;
        // TODO -- do some idempotency so that you don't build two or more stores for the same tenant id
        var settings = new DatabaseSettings
        {
            // You always want the command queues disabled for non-default databases
            CommandQueuesEnabled = false,
            // TODO -- set the AutoCreate here
            DataSource = source,
            Role = MessageStoreRole.Tenant,
            ScheduledJobLockId = _persistence.ScheduledJobLockId,
            SchemaName = _persistence.EnvelopeStorageSchemaName
        };

        store = new PostgresqlMessageStore(settings, _runtime.Options.Durability, source,
            _runtime.LoggerFactory.CreateLogger<PostgresqlMessageStore>(), _sagaTables);
        return store;
    }

    public async Task RefreshAsync()
    {
        if (_persistence.ConnectionStringTenancy != null)
        {
            await _persistence.ConnectionStringTenancy.RefreshAsync();

            foreach (var assignment in _persistence.ConnectionStringTenancy.AllActiveByTenant())
            {
                // TODO -- some idempotency
                if (!_stores.Contains(assignment.TenantId))
                {
                    var store = buildTenantStoreForConnectionString(assignment.Value);
                    store.TenantIds.Fill(assignment.TenantId);
                    
                    if (_runtime.Options.AutoBuildMessageStorageOnStartup != AutoCreate.None)
                    {
                        await store.Admin.MigrateAsync();
                    }
                    
                    _stores = _stores.AddOrUpdate(assignment.TenantId, store);
                }
            }
        }
        else
        {
            await _persistence.DataSourceTenancy!.RefreshAsync();

            foreach (var assignment in _persistence.DataSourceTenancy.AllActiveByTenant())
            {
                // TODO -- some idempotency
                if (!_stores.Contains(assignment.TenantId))
                {
                    var store = buildTenantStoreForDataSource(assignment.Value);
                    store.TenantIds.Fill(assignment.TenantId);
                    
                    if (_runtime.Options.AutoBuildMessageStorageOnStartup != AutoCreate.None)
                    {
                        await store.Admin.MigrateAsync();
                    }
                    
                    _stores = _stores.AddOrUpdate(assignment.TenantId, store);
                }
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