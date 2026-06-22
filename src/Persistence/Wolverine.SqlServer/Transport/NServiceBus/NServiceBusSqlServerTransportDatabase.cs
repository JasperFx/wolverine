using JasperFx;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Descriptors;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Weasel.Core;
using Weasel.Core.Migrations;
using Weasel.SqlServer;
using Wolverine.RDBMS;
using Wolverine.Runtime;

namespace Wolverine.SqlServer.Transport.NServiceBus;

/// <summary>
/// Exposes the NServiceBus interop queue tables as a Weasel <see cref="IDatabase"/> so they
/// participate in Wolverine's resource model and the Weasel command line tooling
/// (db-apply / db-dump / db-assert). Modeled on <see cref="SqlServerTransportDatabase"/>.
/// </summary>
public class NServiceBusSqlServerTransportDatabase : DatabaseBase<SqlConnection>
{
    private readonly NServiceBusSqlServerTransport _transport;
    private readonly string _connectionString;

    internal static SqlConnection BuildConnection(IWolverineRuntime runtime)
    {
        var transport = runtime.Options.NServiceBusSqlServerTransport();
        return new SqlConnection(transport.ResolveConnectionString(runtime));
    }

    public NServiceBusSqlServerTransportDatabase(IWolverineRuntime runtime) : base(
        new MigrationLogger(runtime.LoggerFactory.CreateLogger<NServiceBusSqlServerTransportDatabase>()),
        AutoCreate.CreateOrUpdate, new SqlServerMigrator(), "NServiceBus Sql Server Interop",
        () => BuildConnection(runtime))
    {
        _transport = runtime.Options.NServiceBusSqlServerTransport();
        _connectionString = _transport.ResolveConnectionString(runtime);

        // ReSharper disable once VirtualMemberCallInConstructor
        var descriptor = Describe();
        Id = new DatabaseId(descriptor.ServerName, descriptor.DatabaseName);
    }

    public override DatabaseDescriptor Describe()
    {
        var builder = new SqlConnectionStringBuilder(_connectionString);
        var descriptor = new DatabaseDescriptor
        {
            Engine = "SqlServer",
            ServerName = builder.DataSource ?? string.Empty,
            DatabaseName = builder.InitialCatalog ?? string.Empty,
            Subject = GetType().FullNameInCode(),
            SchemaOrNamespace = _transport.SchemaName
        };

        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.ApplicationName));
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
        return _transport.Queues
            .Select(queue => (IFeatureSchema)new NServiceBusSqlServerQueueFeatureSchema(queue, Migrator))
            .ToArray();
    }
}

internal class NServiceBusSqlServerQueueFeatureSchema : FeatureSchemaBase
{
    public NServiceBusSqlServerQueue Queue { get; }

    public NServiceBusSqlServerQueueFeatureSchema(NServiceBusSqlServerQueue queue, Migrator migrator)
        : base($"NServiceBusSqlServerQueue_{queue.Name}", migrator)
    {
        Queue = queue;
    }

    protected override IEnumerable<ISchemaObject> schemaObjects()
    {
        yield return Queue.QueueTable;
    }
}
