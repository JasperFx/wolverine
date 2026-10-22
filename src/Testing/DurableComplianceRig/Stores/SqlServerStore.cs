using DurableComplianceRig.Infrastructure;
using IntegrationTests;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Wolverine.SqlServer;

namespace DurableComplianceRig.Stores;

/// <summary>
/// Wolverine's own SQL Server message store, with handlers that write through plain ADO.NET -- the same
/// <see cref="RdbmsLedger" /> the PostgreSQL profile uses, so the two plain stores are judged identically.
/// </summary>
public class SqlServerStore : StoreProfile
{
    public static readonly string Schema = "rig_sql" + RigEnvironment.SchemaSuffix;

    private readonly RigDatabase _database = new("sqlserver", Dialect.SqlServer,
        () => new SqlConnection(Servers.SqlServerConnectionString), Schema);

    public override string Name => "SQL Server";
    public override string Key => "sql";
    public override EffectSemantics Semantics => EffectSemantics.AtLeastOnce;
    public override IReadOnlyList<RigDatabase> Databases => [_database];

    public override void ConfigurePersistence(WolverineOptions opts, bool withDatabaseQueues)
    {
        SqlServerQueues.PersistMessages(opts, Schema, withDatabaseQueues);
        opts.Services.AddSingleton(new RdbmsLedger(_database));
        opts.Discovery.IncludeType(typeof(RdbmsRigHandler));
    }

    public override void ConfigureDatabaseQueue(WolverineOptions opts, string queueName) =>
        SqlServerQueues.Configure(opts, queueName);
}

/// <summary>SQL Server queue routing, shared by the plain SQL Server and the EF Core profiles.</summary>
public static class SqlServerQueues
{
    /// <summary>
    /// <c>ISqlServerBackedPersistence.EnableMessageTransport</c> throws NotImplementedException, unlike its
    /// PostgreSQL twin, so the transport comes from <c>UseSqlServerPersistenceAndTransport</c>. The queue tables go in
    /// the store's own schema, which keeps parallel rig runs (RIG_SUFFIX) apart.
    /// </summary>
    public static void PersistMessages(WolverineOptions opts, string schema, bool withDatabaseQueues)
    {
        if (withDatabaseQueues)
        {
            opts.UseSqlServerPersistenceAndTransport(Servers.SqlServerConnectionString, schema, schema).AutoProvision();
        }
        else
        {
            opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, schema);
        }
    }

    public static void Configure(WolverineOptions opts, string queueName)
    {
        opts.PublishMessage<RigMessage>().ToSqlServerQueue(queueName).CustomizeOutgoing(RigIds.Stamp);
        opts.PublishMessage<RigItem>().ToSqlServerQueue(queueName).CustomizeOutgoing(RigIds.Stamp);
        opts.ListenToSqlServerQueue(queueName);
    }
}
