using System.Data.Common;
using ImTools;
using JasperFx;
using JasperFx.Core;
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

    public JasperFx.Descriptors.DatabaseCardinality Cardinality => JasperFx.Descriptors.DatabaseCardinality.Single;

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
        return Task.CompletedTask;
    }

    public Task RefreshLiteAsync()
    {
        return Task.CompletedTask;
    }

    public async ValueTask<IMessageStore> FindAsync(string tenantId)
    {
        if (_stores.TryFind(tenantId, out var store))
        {
            return store;
        }

        // SQLite typically uses file-based databases, so we build from connection string
        var connectionString = _persistence.ConnectionString ?? throw new InvalidOperationException("Connection string is required for tenanted SQLite stores");
        store = buildTenantStoreForConnectionString(connectionString);

        store.TenantIds.Fill(tenantId);

        if (_runtime.Options.AutoBuildMessageStorageOnStartup != AutoCreate.None)
        {
            await store.Admin.MigrateAsync();
        }

        _stores = _stores.AddOrUpdate(tenantId, store);
        return store;
    }

    private SqliteMessageStore buildTenantStoreForConnectionString(string connectionString)
    {
        // var dataSource = Microsoft.Data.Sqlite.SqliteFactory.Instance.CreateDataSource(connectionString);
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
