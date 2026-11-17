using JasperFx;
using JasperFx.Core.Reflection;
using JasperFx.Descriptors;
using Marten;
using Marten.Storage;
using Microsoft.Extensions.Logging;
using Npgsql;
using Weasel.Postgresql;
using Wolverine.Persistence.Durability;
using Wolverine.Postgresql;
using Wolverine.RDBMS;
using Wolverine.RDBMS.MultiTenancy;
using Wolverine.Runtime;

namespace Wolverine.Marten;

/// <summary>
///     Built to support separate stores in Marten
/// </summary>
/// <typeparam name="T"></typeparam>
internal class MartenMessageDatabaseSource<T> : MartenMessageDatabaseSource where T : IDocumentStore
{
    public MartenMessageDatabaseSource(string schemaName, AutoCreate autoCreate, T store, IWolverineRuntime runtime) :
        base(schemaName, autoCreate, store, runtime)
    {
    }
}

/// <summary>
///     One Wolverine message store per Marten tenant database. Marten makes it legal to store multiple tenants
///     in one database, so it's not 1 to 1 on tenant to database.
/// </summary>
internal class MartenMessageDatabaseSource : TenantedMessageDatabaseSource<IMartenDatabase>
{
    private readonly string _schemaName;
    private readonly IDocumentStore _store;

    public MartenMessageDatabaseSource(
        string schemaName,
        AutoCreate autoCreate,
        IDocumentStore store,
        IWolverineRuntime runtime) : base(autoCreate, runtime)
    {
        _schemaName = schemaName;
        _store = store;
    }

    public override DatabaseCardinality Cardinality => _store.Options.Tenancy.Cardinality;

    protected override ValueTask<IMartenDatabase> findDatabaseAsync(string tenantId)
    {
        return _store.Storage.FindOrCreateDatabase(tenantId);
    }

    protected override async ValueTask<IReadOnlyList<IMartenDatabase>> allDatabasesAsync()
    {
        return await _store.Storage.AllDatabases();
    }

    protected override string identifierFor(IMartenDatabase database)
    {
        return database.Identifier;
    }

    protected override IMessageStore buildMessageStore(IMartenDatabase database)
    {
        var settings = new DatabaseSettings
        {
            SchemaName = _schemaName,
            Role = MessageStoreRole.Tenant,
            AutoCreate = AutoCreate,
            CommandQueuesEnabled = false,
            DataSource = database.As<PostgresqlDatabase>().DataSource
        };

        // The tenant database's data source is Marten's, so Marten disposes it when it should
        var store = new PostgresqlMessageStore(settings, Runtime.Options.Durability,
            database.As<PostgresqlDatabase>().DataSource,
            Runtime.LoggerFactory.CreateLogger<PostgresqlMessageStore>(), ownsDataSource: false)
        {
            Name = database.Identifier ?? new NpgsqlConnectionStringBuilder(settings.ConnectionString).Database!
        };

        store.TenantIds.AddRange(database.TenantIds.Distinct());

        return store;
    }
}
