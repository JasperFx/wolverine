using IntegrationTests;
using JasperFx.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Weasel.Postgresql;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Postgresql;
using Wolverine.RDBMS.Transport;

namespace PostgresqlTests.Transport;

public class external_message_tables : ExternalTableTransportCompliance
{
    public external_message_tables(ITestOutputHelper output) : base(output)
    {
    }

    protected override string connectionString => Servers.PostgresConnectionString;
    protected override string idColumnType => "uuid";
    protected override string bodyColumnType => "jsonb";
    protected override string timestampColumnType => "timestamp with time zone";
    protected override string messageTypeColumnType => "varchar";

    protected override void configurePersistence(WolverineOptions opts, string connectionString, string schemaName) =>
        opts.UsePostgresqlPersistenceAndTransport(connectionString, schemaName);

    protected override async ValueTask dropSchemaAsync(string connectionString, string[] schemas, CancellationToken cancellationToken)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        foreach (var schema in schemas)
        {
            await conn.DropSchemaAsync(schema, cancellationToken);
        }
    }
}

public static class Bootstrapping
{
    public static void Configure()
    {
        #region sample_configuring_external_database_messaging
        var builder = Host.CreateApplicationBuilder();
        builder.UseWolverine(opts =>
        {
            opts.UsePostgresqlPersistenceAndTransport(builder.Configuration.GetConnectionString("postgres")!);

            // Or choose a different provider; MySql, Sqlite, SqlServer, and Oracle are supported.
            // opts.UseSqlServerPersistenceAndTransport(builder.Configuration.GetConnectionString("sqlserver"));
            // opts.UseMySqlPersistenceAndTransport(builder.Configuration.GetConnectionString("mysql"));
            // opts.UseSqlitePersistenceAndTransport(builder.Configuration.GetConnectionString("sqlite"));
            // Oracle has no combined "PersistenceAndTransport" helper; its database queue transport
            // is opted into fluently. The external table listening below needs only the persistence.
            // opts.PersistMessagesWithOracle(builder.Configuration.GetConnectionString("oracle")!)
            //     .EnableMessageTransport();

            // Or
            // opts.Services
            //     .AddMarten(builder.Configuration.GetConnectionString("postgres"))
            //     .IntegrateWithWolverine();
            
            // Directing Wolverine to "listen" for messages in an externally controlled table
            // You have to explicitly tell Wolverine about the schema name and table name
            opts.ListenForMessagesFromExternalDatabaseTable("exports", "messaging", table =>
                {
                    // The primary key column for this table, default is "id"
                    table.IdColumnName = "id";

                    // What column has the actual JSON data? Default is "json"
                    table.JsonBodyColumnName = "body";

                    // Optionally tell Wolverine that the message type name is this
                    // column. 
                    table.MessageTypeColumnName = "message_type";

                    // Add a column for the current time when a message was inserted
                    // Strictly for diagnostics
                    table.TimestampColumnName = "added";

                    // How often should Wolverine poll this table? Default is 10 seconds
                    table.PollingInterval = 1.Seconds();

                    // Maximum number of messages that each node should try to pull in at 
                    // any one time. Default is 100
                    table.MessageBatchSize = 50;

                    // Is Wolverine allowed to try to apply automatic database migrations for this
                    // table at startup time? Default is true.
                    // Also overridden by WolverineOptions.AutoBuildMessageStorageOnStartup
                    table.AllowWolverineControl = true;

                    // Wolverine uses a database advisory lock so that only one node at a time
                    // can ever be polling for messages at any one time. Default is 12000
                    // It might release contention to vary the advisory lock if you have multiple
                    // incoming tables or applications targeting the same database
                    table.AdvisoryLock = 12001;
                    
                    // Tell Wolverine what the default message type is coming from this
                    // table to aid in deserialization
                    table.MessageType = typeof(ExternalMessage);
                    
                    
                })
                
                // Just showing that you have all the normal options for configuring and
                // fine tuning the behavior of a message listening endpoint here
                .Sequential();
        });

        #endregion
    }
}

public class ExternalMessage;
