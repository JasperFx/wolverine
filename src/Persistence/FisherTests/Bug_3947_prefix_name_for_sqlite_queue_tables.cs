using Fisher;
using JasperFx;
using JasperFx.Core;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Fisher;
using Wolverine.Runtime;
using Wolverine.Sqlite;
using Wolverine.Sqlite.Transport;
using Wolverine.Tracking;

namespace FisherTests;

/// <summary>
/// GH-3947. FisherIntegration.TransportSchemaName used to be accepted and ignored: SQLite has no schemas,
/// so there was nothing for it to name. FisherIntegration.PrefixName now prefixes the SQLite queue and
/// scheduled-message tables so that separate sets of them can share one Fisher file. It is opt in, and
/// the obsolete TransportSchemaName forwards to it, so only an explicit setting changes any table name.
/// </summary>
public class Bug_3947_prefix_name_for_sqlite_queue_tables
{
    private const string QueueName = "orders";

    [Fact]
    public async Task keeps_the_historical_queue_table_names_when_nothing_is_set()
    {
        using var database = Servers.CreateDatabase("bug_3947_default");
        using var host = await startHost(database, _ => { });

        await sendMessages(host, "default");

        await using var connection = await openAsync(database);
        (await tableCountAsync(connection, $"wolverine_queue_{QueueName}")).ShouldBe(1);
        (await tableCountAsync(connection, $"wolverine_queue_{QueueName}_scheduled")).ShouldBe(1);
    }

    [Fact]
    public async Task an_explicit_main_prefix_keeps_the_historical_queue_table_names()
    {
        using var database = Servers.CreateDatabase("bug_3947_main");
        using var host = await startHost(database, w => w.PrefixName = "main");

        await sendMessages(host, "main");

        await using var connection = await openAsync(database);
        (await tableCountAsync(connection, $"wolverine_queue_{QueueName}")).ShouldBe(1);
        (await tableCountAsync(connection, $"wolverine_queue_{QueueName}_scheduled")).ShouldBe(1);
        (await tableExistsAsync(connection, $"main_wolverine_queue_{QueueName}")).ShouldBeFalse();
    }

    [Fact]
    public async Task prefix_name_prefixes_the_ready_and_scheduled_tables_and_keeps_two_sets_apart_in_one_file()
    {
        using var database = Servers.CreateDatabase("bug_3947_prefix");
        IHost? host = null;

        try
        {
            host = await startHost(database, w => w.PrefixName = "reporting");
            await sendMessages(host, "reporting");
            await host.StopAsync(TestContext.Current.CancellationToken);
            host.Dispose();
            host = null;

            // A second host sharing the same SQLite file can use the same queue name without
            // colliding with or consuming the first host's queue tables.
            host = await startHost(database, w => w.PrefixName = "billing");
            await sendMessages(host, "billing");

            await using var connection = await openAsync(database);
            (await tableCountAsync(connection, $"reporting_wolverine_queue_{QueueName}")).ShouldBe(1);
            (await tableCountAsync(connection, $"reporting_wolverine_queue_{QueueName}_scheduled")).ShouldBe(1);
            (await tableCountAsync(connection, $"billing_wolverine_queue_{QueueName}")).ShouldBe(1);
            (await tableCountAsync(connection, $"billing_wolverine_queue_{QueueName}_scheduled")).ShouldBe(1);
            (await tableExistsAsync(connection, $"wolverine_queue_{QueueName}")).ShouldBeFalse();
        }
        finally
        {
            if (host != null)
            {
                await host.StopAsync(TestContext.Current.CancellationToken);
                host.Dispose();
            }
        }
    }

#pragma warning disable CS0618 // TransportSchemaName is obsolete, and these tests pin what it still does
    [Fact]
    public void the_obsolete_transport_schema_name_forwards_to_prefix_name()
    {
        var integration = new FisherIntegration();

        // Unchanged default for anybody still reading it
        integration.TransportSchemaName.ShouldBe("wolverine_queues");
        integration.PrefixName.ShouldBeNull();

        integration.TransportSchemaName = "Reporting";

        integration.PrefixName.ShouldBe("reporting");
        integration.TransportSchemaName.ShouldBe("reporting");
    }

    [Fact]
    public async Task an_explicit_transport_schema_name_prefixes_the_queue_tables_through_prefix_name()
    {
        using var database = Servers.CreateDatabase("bug_3947_obsolete");
        using var host = await startHost(database, w => w.TransportSchemaName = "reporting");

        await sendMessages(host, "obsolete");

        await using var connection = await openAsync(database);
        (await tableCountAsync(connection, $"reporting_wolverine_queue_{QueueName}")).ShouldBe(1);
        (await tableCountAsync(connection, $"reporting_wolverine_queue_{QueueName}_scheduled")).ShouldBe(1);
    }
#pragma warning restore CS0618

    [Fact]
    public void a_multi_part_prefix_name_is_rejected()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new FisherIntegration { PrefixName = "crm.sales" });
    }

    [Fact]
    public async Task the_documented_configuration_prefixes_the_queue_and_durability_tables()
    {
        using var database = Servers.CreateDatabase("bug_3947_sample");
        var connectionString = database.ConnectionString;

        #region sample_fisher_prefix_name_for_sqlite_queue_tables
        var builder = Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.PublishAllMessages().ToSqliteQueue("orders");

                opts.Services.AddFisher(m => m.Connection(connectionString))
                    .ApplyAllDatabaseChangesOnStartup()
                    .IntegrateWithWolverine(w =>
                    {
                        // Prefixes the SQLite queue tables, so the "orders" queue lives in
                        // reporting_wolverine_queue_orders and reporting_wolverine_queue_orders_scheduled
                        w.PrefixName = "reporting";

                        // Prefixes Wolverine's own inbox, outbox and node tables
                        w.MessageStorageSchemaName = "reporting";
                    });
            });
        #endregion

        using var host = await builder.StartAsync(TestContext.Current.CancellationToken);

        await sendMessages(host, "sample");

        await using var connection = await openAsync(database);
        (await tableCountAsync(connection, $"reporting_wolverine_queue_{QueueName}")).ShouldBe(1);
        (await tableExistsAsync(connection, "reporting_wolverine_incoming_envelopes")).ShouldBeTrue();
    }

    private static async Task<IHost> startHost(FisherTestDatabase database, Action<FisherIntegration> configure)
    {
        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.PublishAllMessages().ToSqliteQueue(QueueName);

                opts.Services.AddFisher(m =>
                    {
                        m.Connection(database.ConnectionString);
                        m.AutoCreateSchemaObjects = AutoCreate.All;
                    })
                    .ApplyAllDatabaseChangesOnStartup()
                    .IntegrateWithWolverine(configure);
            }).StartAsync(TestContext.Current.CancellationToken);
    }

    private static async Task sendMessages(IHost host, string payload)
    {
        var transport = host.GetRuntime().Options.Transports.OfType<SqliteTransport>().Single();
        var queue = transport.Queues[QueueName];
        var endpoint = host.MessageBus().EndpointFor($"sqlite://{QueueName}".ToUri());

        await endpoint.SendAsync(new QueuePrefixMessage($"{payload}-ready"));
        await endpoint.SendAsync(new QueuePrefixMessage($"{payload}-scheduled"), new DeliveryOptions
        {
            ScheduleDelay = TimeSpan.FromHours(1)
        });

        (await queue.CountAsync()).ShouldBe(1);
        (await queue.ScheduledCountAsync()).ShouldBe(1);
    }

    private static async Task<SqliteConnection> openAsync(FisherTestDatabase database)
    {
        var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        return connection;
    }

    private static async Task<long> tableCountAsync(SqliteConnection connection, string tableName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"select count(*) from \"{tableName}\"";
        return Convert.ToInt64(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    private static async Task<bool> tableExistsAsync(SqliteConnection connection, string tableName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "select count(*) from sqlite_master where type = 'table' and name = $name";
        command.Parameters.AddWithValue("$name", tableName);
        return Convert.ToInt64(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken)) > 0;
    }

    public record QueuePrefixMessage(string Name);
}
