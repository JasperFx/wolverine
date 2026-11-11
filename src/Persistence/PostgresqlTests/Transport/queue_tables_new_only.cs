using System.Collections.Concurrent;
using IntegrationTests;
using JasperFx;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Weasel.Core;
using Weasel.Postgresql;
using Weasel.Postgresql.Tables;
using Wolverine;
using Wolverine.Postgresql;
using Wolverine.Postgresql.Transport;
using Wolverine.Tracking;

namespace PostgresqlTests.Transport;

/// <summary>
/// GH-5192. From 7.0 a PostgreSQL queue table Wolverine creates gets the queue_order tie-breaker, but a queue
/// table that already exists without it keeps its layout: adding an identity column rewrites the table under
/// an exclusive lock. The listener then dequeues that table in timestamp order.
/// </summary>
[Collection("Postgresql")]
public class queue_tables_new_only
{
    private const string QueueName = "new_only";
    private static CancellationToken ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task a_queue_table_wolverine_creates_gets_deterministic_ordering_by_default()
    {
        var schema = $"gh5192_new_{Guid.NewGuid():N}";
        await dropSchemaAsync(schema);

        try
        {
            using var host = await startAsync(schema, opts => { });
            var queue = queueOf(host);

            queue.Parent.EnableDeterministicQueueOrdering.ShouldBeTrue();
            (await hasQueueOrderAsync(queue.QueueTable)).ShouldBeTrue();
            (await hasQueueOrderAsync(queue.ScheduledTable)).ShouldBeTrue();
            (await queue.UsesQueueOrderingAsync("check", queue.DataSource)).ShouldBeTrue();
            (await queue.CheckAsync()).ShouldBeTrue();

            await host.StopAsync(ct);
        }
        finally
        {
            await dropSchemaAsync(schema);
        }
    }

    [Fact]
    public async Task an_existing_classic_queue_table_is_adopted_as_it_stands()
    {
        var schema = $"gh5192_adopt_{Guid.NewGuid():N}";
        await dropSchemaAsync(schema);
        NewOnlyHandler.Handled.Clear();

        try
        {
            // A 6.x application: classic tables, with a backlog sitting in the ready queue
            using (var classic = await startAsync(schema,
                       opts => opts.PublishMessage<NewOnlyMessage>().ToPostgresqlQueue(QueueName),
                       x => x.DisableDeterministicQueueOrdering(), listen: false))
            {
                var bus = classic.MessageBus();
                for (var i = 0; i < 10; i++) await bus.PublishAsync(new NewOnlyMessage(i));

                var queue = queueOf(classic);
                await waitUntilAsync(async () => await queue.CountAsync() == 10, "the backlog to reach the queue");
                (await hasQueueOrderAsync(queue.QueueTable)).ShouldBeFalse();

                await classic.StopAsync(ct);
            }

            // The same database after the upgrade, on the 7.0 default
            using var upgraded = await startAsync(schema, opts =>
            {
                opts.Discovery.IncludeType(typeof(NewOnlyHandler));
            });

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
            (await upgradedQueue.UsesQueueOrderingAsync("check", upgradedQueue.DataSource)).ShouldBeFalse();
            await waitUntilAsync(() => Task.FromResult(NewOnlyHandler.Handled.Count == 10),
                "the backlog to be handled");
            NewOnlyHandler.Handled.Keys.OrderBy(x => x).ShouldBe(Enumerable.Range(0, 10));

            await upgraded.StopAsync(ct);
        }
        finally
        {
            await dropSchemaAsync(schema);
        }
    }

    [Fact]
    public async Task an_explicit_opt_in_still_migrates_an_existing_classic_table()
    {
        var schema = $"gh5192_explicit_{Guid.NewGuid():N}";
        await dropSchemaAsync(schema);

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
            (await queue.UsesQueueOrderingAsync("check", queue.DataSource)).ShouldBeTrue();

            await optedIn.StopAsync(ct);
        }
        finally
        {
            await dropSchemaAsync(schema);
        }
    }

    private static async Task<IHost> startAsync(string schema, Action<WolverineOptions> configure,
        Action<PostgresqlPersistenceExpression>? transport = null, bool listen = true)
    {
        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery();
                var expression = opts.UsePostgresqlPersistenceAndTransport(Servers.PostgresConnectionString, schema,
                    transportSchema: schema).AutoProvision();
                transport?.Invoke(expression);

                if (listen)
                {
                    opts.ListenToPostgresqlQueue(QueueName).PollingInterval(250.Milliseconds());
                }

                configure(opts);
            }).StartAsync(ct);
    }

    private static PostgresqlQueue queueOf(IHost host)
    {
        return host.GetRuntime().Options.Transports.GetOrCreate<PostgresqlTransport>().Queues[QueueName];
    }

    private static async Task<bool> hasQueueOrderAsync(Table table)
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
                          SELECT count(*) FROM information_schema.columns
                          WHERE table_schema = @schema AND table_name = @table AND column_name = 'queue_order';
                          """;
        cmd.Parameters.AddWithValue("schema", table.Identifier.Schema!);
        cmd.Parameters.AddWithValue("table", table.Identifier.Name);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct)) == 1;
    }

    private static async Task<string> migrationScriptAsync(Table table)
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync(ct);
        var migration = await SchemaMigration.DetermineAsync(conn, ct, table);
        using var writer = new StringWriter();
        migration.WriteAllUpdates(writer, new PostgresqlMigrator(), AutoCreate.CreateOrUpdate);
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

    private static async Task dropSchemaAsync(string schema)
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync(ct);
        await conn.DropSchemaAsync(schema, ct);
    }
}

public record NewOnlyMessage(int Number);

public static class NewOnlyHandler
{
    public static readonly ConcurrentDictionary<int, bool> Handled = new();

    public static void Handle(NewOnlyMessage message) => Handled[message.Number] = true;
}
