using JasperFx;
using JasperFx.Descriptors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Polecat;
using Polecat.Storage;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;
using Wolverine.RDBMS.MultiTenancy;
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

/// <summary>
///     One Wolverine message store per Polecat tenant database
/// </summary>
internal class PolecatMessageDatabaseSource : TenantedMessageDatabaseSource<PolecatMessageDatabaseSource.TenantDatabase>
{
    private readonly string _schemaName;
    private readonly IDocumentStore _store;
    private readonly ITenancy _tenancy;

    public PolecatMessageDatabaseSource(
        string schemaName,
        AutoCreate autoCreate,
        IDocumentStore store,
        IWolverineRuntime runtime) : base(autoCreate, runtime)
    {
        _schemaName = schemaName;
        _store = store;

        // Requires Polecat 1.1+ where StoreOptions.Tenancy is public
        _tenancy = store.Options.Tenancy
                   ?? throw new InvalidOperationException(
                       "Polecat store does not have tenancy configured. Use MultiTenantedDatabases() in store setup.");
    }

    public override DatabaseCardinality Cardinality => _tenancy.Cardinality;

    protected override ValueTask<TenantDatabase> findDatabaseAsync(string tenantId)
    {
        var factory = _tenancy.GetConnectionFactory(tenantId);
        var database = _tenancy.GetDatabase(tenantId);
        return ValueTask.FromResult(new TenantDatabase(database.Identifier, factory.ConnectionString));
    }

    protected override ValueTask<IReadOnlyList<TenantDatabase>> allDatabasesAsync()
    {
        IReadOnlyList<TenantDatabase> databases = _tenancy.AllDatabases()
            .Select(x => new TenantDatabase(x.Identifier, x.ConnectionString ?? _store.Options.ConnectionString!))
            .ToList();

        return ValueTask.FromResult(databases);
    }

    protected override string identifierFor(TenantDatabase database)
    {
        return database.Identifier;
    }

    protected override IMessageStore buildMessageStore(TenantDatabase database)
    {
        var settings = new DatabaseSettings
        {
            SchemaName = _schemaName,
            Role = MessageStoreRole.Tenant,
            AutoCreate = AutoCreate,
            CommandQueuesEnabled = false,
            ConnectionString = database.ConnectionString
        };

        var sagaTypes = Runtime.Services.GetServices<SagaTableDefinition>();
        return new SqlServerMessageStore(settings, Runtime.Options.Durability,
            Runtime.LoggerFactory.CreateLogger<SqlServerMessageStore>(), sagaTypes)
        {
            Name = database.Identifier
        };
    }

    /// <summary>
    ///     A Polecat tenant database: its identifier and the connection string to reach it
    /// </summary>
    internal record TenantDatabase(string Identifier, string ConnectionString);
}
