using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Weasel.Core;
using Weasel.Core.Migrations;
using Weasel.SqlServer;
using Wolverine.RDBMS;
using Wolverine.Runtime;

namespace Wolverine.SqlServer.Transport;

public class SqlServerTransportDatabase : DatabaseBase<SqlConnection>
{
    private readonly SqlServerTransport _transport;
    private readonly IWolverineRuntime _runtime;

    internal static SqlConnection BuildConnection(IWolverineRuntime runtime)
    {
        var transport = runtime.Options.SqlServerTransport();
        return new SqlConnection(transport.Settings.ConnectionString);
    }

    public SqlServerTransportDatabase(IWolverineRuntime runtime) : base(new MigrationLogger(runtime.LoggerFactory.CreateLogger<SqlServerTransportDatabase>()), AutoCreate.CreateOrUpdate, new SqlServerMigrator(), "Sql Server Messaging Transport", () => BuildConnection(runtime))
    {
        _transport = runtime.Options.SqlServerTransport();
        _runtime = runtime;
    }

    public override IFeatureSchema[] BuildFeatureSchemas()
    {
        return _transport.Queues.Select(queue => (IFeatureSchema)new SqlServerQueueFeatureSchema(queue, Migrator)).ToArray();
    }
}

internal class SqlServerQueueFeatureSchema : FeatureSchemaBase
{
    public SqlServerQueue Queue { get; }

    public SqlServerQueueFeatureSchema(SqlServerQueue queue, Migrator migrator) : base($"SqlServerQueue_{queue.Name}", migrator)
    {
        Queue = queue;
    }

    protected override IEnumerable<ISchemaObject> schemaObjects()
    {
        yield return Queue.QueueTable;
        yield return Queue.ScheduledTable;
    }
}