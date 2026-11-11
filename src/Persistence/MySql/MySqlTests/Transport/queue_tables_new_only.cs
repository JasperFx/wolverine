using System.Collections.Concurrent;
using IntegrationTests;
using JasperFx;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using MySqlConnector;
using Shouldly;
using Weasel.Core;
using Weasel.MySql;
using Weasel.MySql.Tables;
using Wolverine;
using Wolverine.MySql;
using Wolverine.MySql.Transport;
using Wolverine.Tracking;

namespace MySqlTests.Transport;

/// <summary>
/// GH-5192. From 7.0 a MySQL queue table Wolverine creates gets the queue_order tie-breaker, but a queue table that
/// already exists without it keeps its layout: adding the AUTO_INCREMENT column rewrites the table and numbers
/// every row. The listener then dequeues that table in timestamp order.
/// </summary>
[Collection("mysql")]
public class queue_tables_new_only
{
    private const string QueueName = "new_only";
    private static CancellationToken ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task a_queue_table_wolverine_creates_gets_deterministic_ordering_by_default()
    {
        var schema = await newSchemaAsync();

        try
        {
            using var host = await startAsync(schema, _ => { });
            var queue = queueOf(host);

            queue.Parent.EnableDeterministicQueueOrdering.ShouldBeTrue();
            (await hasQueueOrderAsync(queue.QueueTable)).ShouldBeTrue();
            (await hasQueueOrderAsync(queue.ScheduledTable)).ShouldBeTrue();
            (await queue.UsesQueueOrderingAsync("check", queue.Parent.Store!.MySqlDataSource)).ShouldBeTrue();
            (await queue.CheckAsync()).ShouldBeTrue();

            await host.StopAsync(ct);
        }
        finally
        {
            await executeAsync($"DROP DATABASE IF EXISTS {schema}");
        }
    }

    [Fact]
    public async Task an_existing_classic_queue_table_is_adopted_as_it_stands()
    {
        var schema = await newSchemaAsync();
        MySqlNewOnlyHandler.Handled.Clear();

        try
        {
            // A 6.x application: classic tables, with a backlog sitting in the ready queue
            using (var classic = await startAsync(schema,
                       opts => opts.PublishMessage<MySqlNewOnlyMessage>().ToMySqlQueue(QueueName),
                       x => x.DisableDeterministicQueueOrdering(), listen: false))
            {
                var bus = classic.MessageBus();
                for (var i = 0; i < 10; i++) await bus.PublishAsync(new MySqlNewOnlyMessage(i));

                var queue = queueOf(classic);
                await waitUntilAsync(async () => await queue.CountAsync() == 10, "the backlog to reach the queue");
                (await hasQueueOrderAsync(queue.QueueTable)).ShouldBeFalse();

                await classic.StopAsync(ct);
            }

            // The same database after the upgrade, on the 7.0 default
            using var upgraded = await startAsync(schema,
                opts => opts.Discovery.IncludeType(typeof(MySqlNewOnlyHandler)));

            var upgradedQueue = queueOf(upgraded);
            upgradedQueue.Parent.EnableDeterministicQueueOrdering.ShouldBeTrue();

            // No migration, through either the queue or the message store
            (await migrationScriptAsync(upgradedQueue.QueueTable)).ShouldBeEmpty();
            (await migrationScriptAsync(upgradedQueue.ScheduledTable)).ShouldBeEmpty();
            await upgraded.GetRuntime().Storage.Admin.MigrateAsync();
            (await hasQueueOrderAsync(upgradedQueue.QueueTable)).ShouldBeFalse();
            (await hasQueueOrderAsync(upgradedQueue.ScheduledTable)).ShouldBeFalse();
            (await upgradedQueue.CheckAsync()).ShouldBeTrue();

            // ...and the listener reads the classic table with the classic SQL
            (await upgradedQueue.UsesQueueOrderingAsync("check", upgradedQueue.Parent.Store!.MySqlDataSource))
                .ShouldBeFalse();
            await waitUntilAsync(() => Task.FromResult(MySqlNewOnlyHandler.Handled.Count == 10),
                "the backlog to be handled");
            MySqlNewOnlyHandler.Handled.Keys.OrderBy(x => x).ShouldBe(Enumerable.Range(0, 10));

            await upgraded.StopAsync(ct);
        }
        finally
        {
            await executeAsync($"DROP DATABASE IF EXISTS {schema}");
        }
    }

    [Fact]
    public async Task an_explicit_opt_in_still_migrates_an_existing_classic_table()
    {
        var schema = await newSchemaAsync();

        try
        {
            using (var classic = await startAsync(schema, _ => { }, x => x.DisableDeterministicQueueOrdering()))
            {
                (await hasQueueOrderAsync(queueOf(classic).QueueTable)).ShouldBeFalse();
                await classic.StopAsync(ct);
            }

            using var optedIn = await startAsync(schema, _ => { }, x => x.EnableDeterministicQueueOrdering());
            var queue = queueOf(optedIn);

            (await hasQueueOrderAsync(queue.QueueTable)).ShouldBeTrue();
            (await hasQueueOrderAsync(queue.ScheduledTable)).ShouldBeTrue();

            await optedIn.StopAsync(ct);
        }
        finally
        {
            await executeAsync($"DROP DATABASE IF EXISTS {schema}");
        }
    }

    private static async Task<IHost> startAsync(string schema, Action<WolverineOptions> configure,
        Action<MySqlPersistenceExpression>? transport = null, bool listen = true)
    {
        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery();
                var expression = opts.UseMySqlPersistenceAndTransport(Servers.MySqlConnectionString, schema,
                    transportSchema: schema).AutoProvision();
                transport?.Invoke(expression);

                if (listen)
                {
                    opts.ListenToMySqlQueue(QueueName).PollingInterval(250.Milliseconds());
                }

                configure(opts);
            }).StartAsync(ct);
    }

    private static MySqlQueue queueOf(IHost host)
    {
        return host.GetRuntime().Options.Transports.OfType<MySqlTransport>().Single().Queues[QueueName];
    }

    private static async Task<bool> hasQueueOrderAsync(Table table)
    {
        await using var conn = new MySqlConnection(Servers.MySqlConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
                          SELECT COUNT(*) FROM information_schema.columns
                          WHERE table_schema = @schema AND table_name = @table AND column_name = 'queue_order';
                          """;
        cmd.Parameters.AddWithValue("@schema", table.Identifier.Schema!);
        cmd.Parameters.AddWithValue("@table", table.Identifier.Name);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct)) == 1;
    }

    private static async Task<string> migrationScriptAsync(Table table)
    {
        await using var conn = new MySqlConnection(Servers.MySqlConnectionString);
        await conn.OpenAsync(ct);
        var migration = await SchemaMigration.DetermineAsync(conn, ct, table);
        using var writer = new StringWriter();
        migration.WriteAllUpdates(writer, new MySqlMigrator(), AutoCreate.CreateOrUpdate);
        return writer.ToString().Trim();
    }

    private static async Task waitUntilAsync(Func<Task<bool>> condition, string what)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (!await condition())
        {
            if (DateTimeOffset.UtcNow > deadline) throw new TimeoutException($"Timed out waiting for {what}");
            await Task.Delay(100, ct);
        }
    }

    private static async Task<string> newSchemaAsync()
    {
        var schema = $"gh5192_{Guid.NewGuid():N}";
        await executeAsync($"CREATE DATABASE IF NOT EXISTS {schema}");
        return schema;
    }

    private static async Task executeAsync(string sql)
    {
        await using var conn = new MySqlConnection(Servers.MySqlConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

public record MySqlNewOnlyMessage(int Number);

public static class MySqlNewOnlyHandler
{
    public static readonly ConcurrentDictionary<int, bool> Handled = new();

    public static void Handle(MySqlNewOnlyMessage message) => Handled[message.Number] = true;
}
