using Microsoft.Extensions.Logging;
using Npgsql;
using Weasel.Core;
using Weasel.Core.Migrations;
using Weasel.Postgresql;
using Wolverine.RDBMS;
using Wolverine.Runtime;

namespace Wolverine.Postgresql.Transport;

public class PostgresqlTransportDatabase : DatabaseBase<NpgsqlConnection>
{
    private readonly PostgresqlTransport _transport;
    private readonly IWolverineRuntime _runtime;

    internal static NpgsqlConnection BuildConnection(IWolverineRuntime runtime)
    {
        var transport = runtime.Options.PostgresqlTransport();
        return new NpgsqlConnection(transport.Settings.ConnectionString);
    }
    
    public PostgresqlTransportDatabase(IWolverineRuntime runtime) : base(new MigrationLogger(runtime.LoggerFactory.CreateLogger<PostgresqlTransportDatabase>()), AutoCreate.CreateOrUpdate, new PostgresqlMigrator(), "Sql Server Messaging Transport", () => BuildConnection(runtime))
    {
        _transport = runtime.Options.PostgresqlTransport();
        _runtime = runtime;
    }

    public override IFeatureSchema[] BuildFeatureSchemas()
    {
        return _transport.Queues.Select(queue => (IFeatureSchema)new SqlServerQueueFeatureSchema(queue, Migrator)).ToArray();
    }
}

internal class SqlServerQueueFeatureSchema : FeatureSchemaBase
{
    public PostgresqlQueue Queue { get; }

    public SqlServerQueueFeatureSchema(PostgresqlQueue queue, Migrator migrator) : base($"SqlServerQueue_{queue.Name}", migrator)
    {
        Queue = queue;
    }

    protected override IEnumerable<ISchemaObject> schemaObjects()
    {
        yield return Queue.QueueTable;
        yield return Queue.ScheduledTable;
    }
}