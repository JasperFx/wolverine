using ImTools;
using JasperFx;
using JasperFx.Core;
using JasperFx.Descriptors;
using JasperFx.MultiTenancy;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;
using Wolverine.RDBMS.MultiTenancy;
using Wolverine.RDBMS.Sagas;
using Wolverine.Runtime;

namespace Wolverine.MySql;

internal class MySqlTenantedMessageStore : ITenantedMessageSource
{
    private readonly MySqlBackedPersistence _persistence;
    private readonly SagaTableDefinition[] _sagaTables;
    private readonly IWolverineRuntime _runtime;
    private ImHashMap<string, MySqlMessageStore> _stores = ImHashMap<string, MySqlMessageStore>.Empty;

    public MySqlTenantedMessageStore(IWolverineRuntime runtime, MySqlBackedPersistence persistence,
        IEnumerable<SagaTableDefinition> sagaTables)
    {
        _persistence = persistence;
        _sagaTables = sagaTables.ToArray();
        _runtime = runtime;
    }

    public DatabaseCardinality Cardinality => _persistence.DataSourceTenancy?.Cardinality ??
                                              _persistence.ConnectionStringTenancy!.Cardinality;

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
            var connectionString = await _persistence.ConnectionStringTenancy!.FindAsync(tenantId);
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

    private MySqlMessageStore buildTenantStoreForConnectionString(string connectionString)
    {
        var dataSource = MySqlDataSourceFactory.Create(connectionString);
        var settings = new DatabaseSettings
        {
            CommandQueuesEnabled = false,
            DataSource = dataSource,
            ConnectionString = connectionString,
            Role = MessageStoreRole.Tenant,
            ScheduledJobLockId = _persistence.ScheduledJobLockId,
            SchemaName = _persistence.EnvelopeStorageSchemaName
        };

        var store = new MySqlMessageStore(settings, _runtime.Options.Durability, dataSource,
            _runtime.LoggerFactory.CreateLogger<MySqlMessageStore>(), _sagaTables);
        store.Name = store.Describe().DatabaseUri().ToString();
        return store;
    }

    private MySqlMessageStore buildTenantStoreForDataSource(MySqlDataSource source)
    {
        var settings = new DatabaseSettings
        {
            CommandQueuesEnabled = false,
            DataSource = source,
            Role = MessageStoreRole.Tenant,
            ScheduledJobLockId = _persistence.ScheduledJobLockId,
            SchemaName = _persistence.EnvelopeStorageSchemaName
        };

        var store = new MySqlMessageStore(settings, _runtime.Options.Durability, source,
            _runtime.LoggerFactory.CreateLogger<MySqlMessageStore>(), _sagaTables);
        return store;
    }

    public async Task RefreshAsync()
    {
        if (_persistence.ConnectionStringTenancy != null)
        {
            await _persistence.ConnectionStringTenancy.RefreshAsync();

            foreach (var assignment in _persistence.ConnectionStringTenancy.AllActiveByTenant())
            {
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
