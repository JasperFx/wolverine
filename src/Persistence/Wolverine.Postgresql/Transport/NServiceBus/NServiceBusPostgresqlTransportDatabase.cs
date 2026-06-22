using JasperFx;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Descriptors;
using Microsoft.Extensions.Logging;
using Npgsql;
using Weasel.Core;
using Weasel.Core.Migrations;
using Weasel.Postgresql;
using Wolverine.RDBMS;
using Wolverine.Runtime;

namespace Wolverine.Postgresql.Transport.NServiceBus;

/// <summary>
/// Exposes the NServiceBus interop queue tables as a Weasel <see cref="IDatabase"/> so they
/// participate in Wolverine's resource model and the Weasel command line tooling
/// (db-apply / db-dump / db-assert).
/// </summary>
public class NServiceBusPostgresqlTransportDatabase : DatabaseBase<NpgsqlConnection>
{
    private readonly NServiceBusPostgresqlTransport _transport;
    private readonly string _connectionString;

    internal static NpgsqlConnection BuildConnection(IWolverineRuntime runtime)
    {
        var transport = runtime.Options.NServiceBusPostgresqlTransport();
        return new NpgsqlConnection(transport.ResolveConnectionString(runtime));
    }

    public NServiceBusPostgresqlTransportDatabase(IWolverineRuntime runtime) : base(
        new MigrationLogger(runtime.LoggerFactory.CreateLogger<NServiceBusPostgresqlTransportDatabase>()),
        AutoCreate.CreateOrUpdate, new PostgresqlMigrator(), "NServiceBus PostgreSQL Interop",
        () => BuildConnection(runtime))
    {
        _transport = runtime.Options.NServiceBusPostgresqlTransport();
        _connectionString = _transport.ResolveConnectionString(runtime);

        // ReSharper disable once VirtualMemberCallInConstructor
        var descriptor = Describe();
        Id = new DatabaseId(descriptor.ServerName, descriptor.DatabaseName);
    }

    public override DatabaseDescriptor Describe()
    {
        var builder = new NpgsqlConnectionStringBuilder(_connectionString);
        var descriptor = new DatabaseDescriptor
        {
            Engine = "PostgreSQL",
            ServerName = builder.Host ?? string.Empty,
            DatabaseName = builder.Database ?? string.Empty,
            Subject = GetType().FullNameInCode(),
            SchemaOrNamespace = _transport.SchemaName
        };

        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.Host!));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.Port));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.Database!));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.Username!));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.ApplicationName!));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.Pooling));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.MinPoolSize));
        descriptor.Properties.Add(OptionsValue.Read(builder, x => x.MaxPoolSize));

        descriptor.Properties.RemoveAll(x => x.Name.ContainsIgnoreCase("password"));
        descriptor.Properties.RemoveAll(x => x.Name.ContainsIgnoreCase("certificate"));

        return descriptor;
    }

    public override IFeatureSchema[] BuildFeatureSchemas()
    {
        return _transport.Queues
            .Select(queue => (IFeatureSchema)new NServiceBusPostgresqlQueueFeatureSchema(queue, Migrator))
            .ToArray();
    }
}

internal class NServiceBusPostgresqlQueueFeatureSchema : FeatureSchemaBase
{
    public NServiceBusPostgresqlQueue Queue { get; }

    public NServiceBusPostgresqlQueueFeatureSchema(NServiceBusPostgresqlQueue queue, Migrator migrator)
        : base($"NServiceBusPostgresqlQueue_{queue.Name}", migrator)
    {
        Queue = queue;
    }

    protected override IEnumerable<ISchemaObject> schemaObjects()
    {
        yield return Queue.QueueTable;
    }
}
