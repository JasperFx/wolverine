using JasperFx;
using JasperFx.Resources;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Sqlite;

namespace SqliteTests;

public class DocumentationSamples
{
    public static async Task<int> SetupSqliteStorage(string[] args)
    {
        #region sample_setup_sqlite_storage

        var builder = WebApplication.CreateBuilder(args);
        var connectionString = builder.Configuration.GetConnectionString("sqlite");

        builder.Host.UseWolverine(opts =>
        {
            // Setting up SQLite-backed message storage
            // This requires a reference to Wolverine.Sqlite
            opts.PersistMessagesWithSqlite(connectionString);

            // Other Wolverine configuration
        });

        // This is rebuilding the persistent storage database schema on startup
        // and also clearing any persisted envelope state
        builder.Host.UseResourceSetupOnStartup();

        var app = builder.Build();

        // Other ASP.Net Core configuration...

        // Using JasperFx opens up command line utilities for managing
        // the message storage
        return await app.RunJasperFxCommands(args);

        #endregion
    }

    public static void SqliteConnectionStringExamples()
    {
        using var host = Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                #region sample_sqlite_connection_string_examples

                // File-based database (recommended)
                opts.PersistMessagesWithSqlite("Data Source=wolverine.db");

                // File-based database in an application data folder
                opts.PersistMessagesWithSqlite("Data Source=./data/wolverine.db");

                #endregion
            }).Build();
    }

    public static async Task UsingSqliteTransport()
    {
        #region sample_using_sqlite_transport

        var builder = Host.CreateApplicationBuilder();
        builder.UseWolverine(opts =>
        {
            var connectionString = builder.Configuration.GetConnectionString("sqlite");
            opts.UseSqlitePersistenceAndTransport(connectionString)

                // Tell Wolverine to build out all necessary queue or scheduled message
                // tables on demand as needed
                .AutoProvision()

                // Optional that may be helpful in testing, but probably bad
                // in production!
                .AutoPurgeOnStartup();

            // Use this extension method to create subscriber rules
            opts.PublishAllMessages().ToSqliteQueue("outbound");

            // Use this to set up queue listeners
            opts.ListenToSqliteQueue("inbound")

                // Optionally specify how many messages to
                // fetch into the listener at any one time
                .MaximumMessagesToReceive(50);
        });

        using var host = builder.Build();
        await host.StartAsync();

        #endregion
    }

    public static void SqliteTransportConnectionStringOnly()
    {
        using var host = Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                #region sample_sqlite_connection_string_only_transport

                opts.UseSqlitePersistenceAndTransport("Data Source=wolverine.db");

                #endregion
            }).Build();
    }

    public static void SqliteStaticTenantConfiguration()
    {
        using var host = Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                #region sample_sqlite_static_tenancy

                opts.PersistMessagesWithSqlite("Data Source=main.db")
                    .RegisterStaticTenants(tenants =>
                    {
                        tenants.Register("red", "Data Source=red.db");
                        tenants.Register("blue", "Data Source=blue.db");
                    })
                    .EnableMessageTransport(x => x.AutoProvision());

                opts.ListenToSqliteQueue("incoming").UseDurableInbox();

                #endregion
            }).Build();
    }

    public static void SqliteMasterTableTenancy()
    {
        using var host = Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                #region sample_sqlite_master_table_tenancy

                opts.PersistMessagesWithSqlite("Data Source=main.db")
                    .UseMasterTableTenancy(seed =>
                    {
                        seed.Register("red", "Data Source=red.db");
                        seed.Register("blue", "Data Source=blue.db");
                    })
                    .EnableMessageTransport(x => x.AutoProvision());

                #endregion
            }).Build();
    }

    public static async Task SqliteTenantSpecificSend(IHost host)
    {
        #region sample_sqlite_tenant_specific_send

        await host.SendAsync(new SampleTenantMessage("hello"), new DeliveryOptions { TenantId = "red" });

        #endregion
    }

    public static void ConfigureSqlitePollingSettings()
    {
        #region sample_sqlite_polling_configuration

        var builder = Host.CreateApplicationBuilder();
        builder.UseWolverine(opts =>
        {
            // Health check message queue/dequeue
            opts.Durability.HealthCheckPollingTime = TimeSpan.FromSeconds(10);

            // Node reassignment checks
            opts.Durability.NodeReassignmentPollingTime = TimeSpan.FromSeconds(5);

            // User queue poll frequency
            opts.Durability.ScheduledJobPollingTime = TimeSpan.FromSeconds(5);
        });

        #endregion
    }

    public static void SetSqliteQueueToBuffered()
    {
        using var host = Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseSqlitePersistenceAndTransport("Data Source=wolverine-buffered.db")
                    .AutoProvision().AutoPurgeOnStartup().DisableInboxAndOutboxOnAll();

                #region sample_setting_sqlite_queue_to_buffered

                opts.ListenToSqliteQueue("sender").BufferedInMemory();

                #endregion
            }).Build();
    }
}

public record SampleTenantMessage(string Name);
