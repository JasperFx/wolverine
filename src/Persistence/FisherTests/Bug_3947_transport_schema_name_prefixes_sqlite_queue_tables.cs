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

public class Bug_3947_transport_schema_name_prefixes_sqlite_queue_tables
{
    private const string QueueName = "orders";

    [Fact]
    public async Task KeepsLegacyQueueTableNamesWhenTransportSchemaNameIsUnset()
    {
        using var database = Servers.CreateDatabase("bug_3947_default");
        using var host = await startHost(database, null);

        await sendMessages(host, "default");

        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        (await tableCountAsync(connection, $"wolverine_queue_{QueueName}")).ShouldBe(1);
        (await tableCountAsync(connection, $"wolverine_queue_{QueueName}_scheduled")).ShouldBe(1);
    }

    [Fact]
    public async Task AnExplicitMainTransportSchemaNameKeepsTheLegacyQueueTableNames()
    {
        using var database = Servers.CreateDatabase("bug_3947_main");
        using var host = await startHost(database, "main");

        await sendMessages(host, "main");

        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        (await tableCountAsync(connection, $"wolverine_queue_{QueueName}")).ShouldBe(1);
        (await tableCountAsync(connection, $"wolverine_queue_{QueueName}_scheduled")).ShouldBe(1);
        (await tableExistsAsync(connection, $"main_wolverine_queue_{QueueName}")).ShouldBeFalse();
    }

    [Fact]
    public async Task PrefixesReadyAndScheduledTablesWhenTransportSchemaNameIsExplicit()
    {
        using var database = Servers.CreateDatabase("bug_3947_prefix");
        IHost? host = null;

        try
        {
            host = await startHost(database, "reporting");
            await sendMessages(host, "reporting");
            await host.StopAsync(TestContext.Current.CancellationToken);
            host.Dispose();
            host = null;

            // A second host sharing the same SQLite file can use the same queue name without
            // colliding with or consuming the first host's queue tables.
            host = await startHost(database, "billing");
            await sendMessages(host, "billing");

            await using var connection = new SqliteConnection(database.ConnectionString);
            await connection.OpenAsync(TestContext.Current.CancellationToken);

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

    private static async Task<IHost> startHost(FisherTestDatabase database, string? transportSchemaName)
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
                    .IntegrateWithWolverine(w =>
                    {
                        if (transportSchemaName != null)
                        {
                            w.TransportSchemaName = transportSchemaName;
                        }
                    });
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
