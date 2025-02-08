using JasperFx.Core;
using JasperFx.Core.Descriptions;
using JasperFx.Core.Reflection;
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

    public SqlServerTransportDatabase(IWolverineRuntime runtime) : base(new MigrationLogger(runtime.LoggerFactory.CreateLogger<SqlServerTransportDatabase>()), JasperFx.AutoCreate.CreateOrUpdate, new SqlServerMigrator(), "Sql Server Messaging Transport", () => BuildConnection(runtime))
    {
        _transport = runtime.Options.SqlServerTransport();
        _runtime = runtime;
    }
    
    public override DatabaseDescriptor Describe()
    {
        var builder = new SqlConnectionStringBuilder(_transport.Settings.ConnectionString);
        var descriptor = new DatabaseDescriptor()
        {
            Engine = "SqlServer",
            ServerName = builder.DataSource ?? string.Empty,
            DatabaseName = builder.InitialCatalog ?? string.Empty,
            Subject = GetType().FullNameInCode(),
            SchemaOrNamespace = _transport.TransportSchemaName
        };

        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.ApplicationName));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.Enlist));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.PersistSecurityInfo));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.Pooling));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.MinPoolSize));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.MaxPoolSize));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.CommandTimeout));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.TrustServerCertificate));

        descriptor.Properties.RemoveAll(x => x.Name.ContainsIgnoreCase("password"));
        descriptor.Properties.RemoveAll(x => x.Name.ContainsIgnoreCase("certificate"));

        return descriptor;
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