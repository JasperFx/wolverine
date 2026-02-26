using System.Data.Common;
using ImTools;
using JasperFx;
using JasperFx.Core;
using JasperFx.Descriptors;
using JasperFx.MultiTenancy;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;
using Wolverine.RDBMS.MultiTenancy;
using Wolverine.RDBMS.Sagas;
using Wolverine.Runtime;

namespace Wolverine.Sqlite;

internal class SqliteTenantedMessageStore : ITenantedMessageSource
{
    private readonly SqliteBackedPersistence _persistence;
    private readonly SagaTableDefinition[] _sagaTables;
    private readonly IWolverineRuntime _runtime;
    private ImHashMap<string, SqliteMessageStore> _stores = ImHashMap<string, SqliteMessageStore>.Empty;

    public SqliteTenantedMessageStore(IWolverineRuntime runtime, SqliteBackedPersistence persistence,
        SagaTableDefinition[] sagaTables)
    {
        _persistence = persistence;
        _sagaTables = sagaTables;
        _runtime = runtime;
    }

    public DatabaseCardinality Cardinality => _persistence.ConnectionStringTenancy?.Cardinality ?? DatabaseCardinality.Single;

    public IReadOnlyList<IMessageStore> AllActive()
    {
        return _stores.Enumerate().Select(x => x.Value).ToList();
    }

    public IReadOnlyList<Assignment<IMessageStore>> AllActiveByTenant()
    {
        return _stores.Enumerate().Select(x => new Assignment<IMessageStore>(x.Key, x.Value)).ToList();
    }

    public Task RefreshAsync()
    {
        return RefreshAsync(true);
    }

    public Task RefreshLiteAsync()
    {
        return RefreshAsync(false);
    }

    public async ValueTask<IMessageStore> FindAsync(string tenantId)
    {
        if (_stores.TryFind(tenantId, out var store))
        {
            return store;
        }

        if (_persistence.ConnectionStringTenancy == null)
        {
            throw new InvalidOperationException("No multi-tenancy configured for SQLite");
        }

        var connectionString = await _persistence.ConnectionStringTenancy.FindAsync(tenantId);
        store = buildTenantStoreForConnectionString(connectionString);

        store.TenantIds.Fill(tenantId);

        if (_runtime.Options.AutoBuildMessageStorageOnStartup != AutoCreate.None)
        {
            await store.Admin.MigrateAsync();
        }

        _stores = _stores.AddOrUpdate(tenantId, store);
        return store;
    }

    private async Task RefreshAsync(bool withMigration)
    {
        if (_persistence.ConnectionStringTenancy == null)
        {
            return;
        }

        await _persistence.ConnectionStringTenancy.RefreshAsync();

        foreach (var assignment in _persistence.ConnectionStringTenancy.AllActiveByTenant())
        {
            if (_stores.Contains(assignment.TenantId)) continue;

            var store = buildTenantStoreForConnectionString(assignment.Value);
            store.TenantIds.Fill(assignment.TenantId);

            if (withMigration && _runtime.Options.AutoBuildMessageStorageOnStartup != AutoCreate.None)
            {
                await store.Admin.MigrateAsync();
            }

            _stores = _stores.AddOrUpdate(assignment.TenantId, store);
        }
    }

    private SqliteMessageStore buildTenantStoreForConnectionString(string connectionString)
    {
        SqliteConnectionStringPolicy.AssertFileBased(connectionString, "tenant connection string");

        var dataSource = new WolverineSqliteDataSource(connectionString);
        var settings = new DatabaseSettings
        {
            CommandQueuesEnabled = false,
            DataSource = dataSource,
            ConnectionString = connectionString,
            Role = MessageStoreRole.Tenant,
            ScheduledJobLockId = _persistence.ScheduledJobLockId,
            SchemaName = _persistence.EnvelopeStorageSchemaName
        };

        var store = new SqliteMessageStore(settings, _runtime.Options.Durability, dataSource,
            _runtime.LoggerFactory.CreateLogger<SqliteMessageStore>(), _sagaTables);
        store.Name = store.Describe().DatabaseUri().ToString();
        return store;
    }
}
