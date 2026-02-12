using ImTools;
using JasperFx;
using JasperFx.Core;
using JasperFx.Descriptors;
using JasperFx.MultiTenancy;
using Microsoft.Extensions.Logging;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;
using Wolverine.RDBMS.MultiTenancy;
using Wolverine.RDBMS.Sagas;
using Wolverine.Runtime;

namespace Wolverine.Oracle;

internal class OracleTenantedMessageStore : ITenantedMessageSource
{
    private readonly OracleBackedPersistence _persistence;
    private readonly SagaTableDefinition[] _sagaTables;
    private readonly IWolverineRuntime _runtime;
    private ImHashMap<string, OracleMessageStore> _stores = ImHashMap<string, OracleMessageStore>.Empty;

    public OracleTenantedMessageStore(IWolverineRuntime runtime, OracleBackedPersistence persistence,
        IEnumerable<SagaTableDefinition> sagaTables)
    {
        _persistence = persistence;
        _sagaTables = sagaTables.ToArray();
        _runtime = runtime;
    }

    public DatabaseCardinality Cardinality => _persistence.ConnectionStringTenancy?.Cardinality ?? DatabaseCardinality.Single;

    public async ValueTask<IMessageStore> FindAsync(string tenantId)
    {
        if (_stores.TryFind(tenantId, out var store))
        {
            return store;
        }

        var connectionString = await _persistence.ConnectionStringTenancy!.FindAsync(tenantId);
        store = buildTenantStoreForConnectionString(connectionString);

        store.TenantIds.Fill(tenantId);

        if (_runtime.Options.AutoBuildMessageStorageOnStartup != AutoCreate.None)
        {
            await store.Admin.MigrateAsync();
        }

        _stores = _stores.AddOrUpdate(tenantId, store);
        return store;
    }

    private OracleMessageStore buildTenantStoreForConnectionString(string connectionString)
    {
        var dataSource = new OracleDataSource(connectionString);
        var settings = new DatabaseSettings
        {
            CommandQueuesEnabled = false,
            ConnectionString = connectionString,
            Role = MessageStoreRole.Tenant,
            ScheduledJobLockId = _persistence.ScheduledJobLockId,
            SchemaName = _persistence.EnvelopeStorageSchemaName
        };

        var store = new OracleMessageStore(settings, _runtime.Options.Durability, dataSource,
            _runtime.LoggerFactory.CreateLogger<OracleMessageStore>(), _sagaTables);
        store.Name = store.Describe().DatabaseUri().ToString();
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
        if (_persistence.ConnectionStringTenancy != null)
        {
            await _persistence.ConnectionStringTenancy.RefreshAsync();

            foreach (var assignment in _persistence.ConnectionStringTenancy.AllActiveByTenant())
            {
                if (!_stores.Contains(assignment.TenantId))
                {
                    var store = buildTenantStoreForConnectionString(assignment.Value);
                    store.TenantIds.Fill(assignment.TenantId);

                    if (withMigration && _runtime.Options.AutoBuildMessageStorageOnStartup != AutoCreate.None)
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
