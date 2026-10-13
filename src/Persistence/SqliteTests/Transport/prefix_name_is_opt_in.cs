using JasperFx.Core;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Sqlite;
using Wolverine.Sqlite.Transport;
using Wolverine.Tracking;

namespace SqliteTests.Transport;

/// <summary>
/// GH-3947. SQLite has no schemas, so PrefixName() separates sets of queue tables inside one database
/// file by prefixing the queue and scheduled-message table names. It is opt in: without it, or with
/// SQLite's built-in "main", the tables keep their historical names and the messages already in them.
/// </summary>
public class prefix_name_is_opt_in : SqliteContext
{
    private const string QueueName = "orders";

    private static CancellationToken ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task without_a_prefix_the_queue_tables_keep_their_historical_names()
    {
        using var database = Servers.CreateDatabase(nameof(prefix_name_is_opt_in));
        using var host = await startAsync(database, prefixName: null);

        var queue = queueFor(host);
        queue.QueueTable.Identifier.Name.ShouldBe($"wolverine_queue_{QueueName}");
        queue.ScheduledTable.Identifier.Name.ShouldBe($"wolverine_queue_{QueueName}_scheduled");

        await sendReadyAndScheduledAsync(queue);

        (await rowsInAsync(database, $"wolverine_queue_{QueueName}")).ShouldBe(1);
        (await rowsInAsync(database, $"wolverine_queue_{QueueName}_scheduled")).ShouldBe(1);
    }

    [Fact]
    public async Task main_means_no_prefix()
    {
        using var database = Servers.CreateDatabase(nameof(prefix_name_is_opt_in));
        using var host = await startAsync(database, prefixName: "main");

        await sendReadyAndScheduledAsync(queueFor(host));

        (await rowsInAsync(database, $"wolverine_queue_{QueueName}")).ShouldBe(1);
        (await tableExistsAsync(database, $"main_wolverine_queue_{QueueName}")).ShouldBeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task the_prefix_applies_to_the_ready_and_scheduled_tables_through_both_configuration_routes(
        bool usePersistenceConfiguration)
    {
        using var database = Servers.CreateDatabase(nameof(prefix_name_is_opt_in));
        using var host = await startAsync(database, prefixName: "Reporting", usePersistenceConfiguration);

        var queue = queueFor(host);

        // Lower-cased, like every other SQLite identifier Wolverine builds
        queue.QueueTable.Identifier.Name.ShouldBe($"reporting_wolverine_queue_{QueueName}");
        queue.ScheduledTable.Identifier.Name.ShouldBe($"reporting_wolverine_queue_{QueueName}_scheduled");

        await sendReadyAndScheduledAsync(queue);

        (await rowsInAsync(database, $"reporting_wolverine_queue_{QueueName}")).ShouldBe(1);
        (await rowsInAsync(database, $"reporting_wolverine_queue_{QueueName}_scheduled")).ShouldBe(1);
        (await tableExistsAsync(database, $"wolverine_queue_{QueueName}")).ShouldBeFalse();
    }

    [Fact]
    public async Task two_prefixes_keep_two_sets_of_queue_tables_apart_in_one_file()
    {
        using var database = Servers.CreateDatabase(nameof(prefix_name_is_opt_in));

        using (var reporting = await startAsync(database, prefixName: "reporting"))
        {
            await sendReadyAndScheduledAsync(queueFor(reporting));
            await reporting.StopAsync(ct);
        }

        using var billing = await startAsync(database, prefixName: "billing");
        var queue = queueFor(billing);

        // Same queue name, same file, and none of the reporting messages are visible here
        (await queue.CountAsync()).ShouldBe(0);
        (await queue.ScheduledCountAsync()).ShouldBe(0);

        await sendReadyAndScheduledAsync(queue);

        (await rowsInAsync(database, $"reporting_wolverine_queue_{QueueName}")).ShouldBe(1);
        (await rowsInAsync(database, $"billing_wolverine_queue_{QueueName}")).ShouldBe(1);
    }

    [Fact]
    public void a_multi_part_prefix_is_rejected()
    {
        var transport = new SqliteTransport();
        Should.Throw<ArgumentOutOfRangeException>(() => transport.PrefixName = "crm.sales");
    }

    private static async Task<IHost> startAsync(SqliteTestDatabase database, string? prefixName,
        bool usePersistenceConfiguration = false)
    {
        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Discovery.DisableConventionalDiscovery();
                opts.Durability.ScheduledJobFirstExecution = 1.Hours();
                opts.Durability.ScheduledJobPollingTime = 1.Hours();

                // Publish-only, so nothing in the host drains the queue the tests count
                if (usePersistenceConfiguration)
                {
                    var persistence = opts.PersistMessagesWithSqlite(database.ConnectionString);
                    opts.PublishAllMessages().ToSqliteQueue(QueueName);
                    persistence.EnableMessageTransport(expression =>
                    {
                        if (prefixName != null) expression.PrefixName(prefixName);
                    });
                }
                else
                {
                    var expression = opts.UseSqlitePersistenceAndTransport(database.ConnectionString);
                    opts.PublishAllMessages().ToSqliteQueue(QueueName);
                    if (prefixName != null) expression.PrefixName(prefixName);
                }
            }).StartAsync(ct);
    }

    private static SqliteQueue queueFor(IHost host)
    {
        return host.GetRuntime().Options.Transports.GetOrCreate<SqliteTransport>().Queues[QueueName];
    }

    private static async Task sendReadyAndScheduledAsync(SqliteQueue queue)
    {
        await queue.SendAsync(ObjectMother.Envelope());

        var scheduled = ObjectMother.Envelope();
        scheduled.ScheduledTime = DateTimeOffset.UtcNow.AddHours(1);
        await queue.SendAsync(scheduled);

        (await queue.CountAsync()).ShouldBe(1);
        (await queue.ScheduledCountAsync()).ShouldBe(1);
    }

    private static async Task<long> rowsInAsync(SqliteTestDatabase database, string table)
    {
        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = $"select count(*) from \"{table}\"";
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct));
    }

    private static async Task<bool> tableExistsAsync(SqliteTestDatabase database, string table)
    {
        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "select count(*) from sqlite_master where type = 'table' and name = $name";
        command.Parameters.AddWithValue("$name", table);
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct)) > 0;
    }
}
