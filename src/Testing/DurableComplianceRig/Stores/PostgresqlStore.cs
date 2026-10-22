using DurableComplianceRig.Infrastructure;
using IntegrationTests;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Wolverine;
using Wolverine.Postgresql;

namespace DurableComplianceRig.Stores;

/// <summary>Wolverine's own PostgreSQL message store, with handlers that write through plain ADO.NET.</summary>
public class PostgresqlStore : StoreProfile
{
    public static readonly string Schema = "rig_pg" + RigEnvironment.SchemaSuffix;

    private readonly RigDatabase _database = new("postgresql", Dialect.Postgresql,
        () => new NpgsqlConnection(Servers.PostgresConnectionString), Schema);

    public override string Name => "PostgreSQL";
    public override string Key => "pg";
    public override EffectSemantics Semantics => EffectSemantics.AtLeastOnce;
    public override IReadOnlyList<RigDatabase> Databases => [_database];

    public override void ConfigurePersistence(WolverineOptions opts, bool withDatabaseQueues)
    {
        var persistence = opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, Schema);
        if (withDatabaseQueues) persistence.EnableMessageTransport(x => x.AutoProvision());
        opts.Services.AddSingleton(new RdbmsLedger(_database));
        opts.Discovery.IncludeType(typeof(RdbmsRigHandler));
    }

    public override void ConfigureDatabaseQueue(WolverineOptions opts, string queueName)
    {
        opts.PublishMessage<RigMessage>().ToPostgresqlQueue(queueName).CustomizeOutgoing(RigIds.Stamp);
        opts.PublishMessage<RigItem>().ToPostgresqlQueue(queueName).CustomizeOutgoing(RigIds.Stamp);
        opts.ListenToPostgresqlQueue(queueName);
    }
}
