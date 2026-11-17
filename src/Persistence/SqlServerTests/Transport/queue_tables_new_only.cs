using System.Collections.Concurrent;
using IntegrationTests;
using JasperFx;
using JasperFx.Core;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Weasel.Core;
using Weasel.SqlServer;
using Weasel.SqlServer.Tables;
using Wolverine;
using Wolverine.SqlServer;
using Wolverine.SqlServer.Transport;
using Wolverine.Tracking;

namespace SqlServerTests.Transport;

/// <summary>
///     GH-5192. From 7.0 a Sql Server queue table Wolverine creates gets the OptimizeQueueThroughput() layout, and
///     on the classic layout the queue_order tie-breaker. A queue table that already exists keeps the layout it
///     has: rebuilding it, or numbering its rows with a new identity, holds an exclusive lock on it, and two
///     applications that disagreed would rebuild it back and forth. The listener follows the table it finds.
/// </summary>
public class queue_tables_new_only
{
    private const string QueueName = "new_only";
    private static CancellationToken ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task a_queue_table_wolverine_creates_gets_the_throughput_layout_by_default()
    {
        var schema = $"gh5192_new_{Guid.NewGuid():N}";
        await dropSchemaAsync(schema);

        try
        {
            using var host = await startAsync(schema, _ => { });
            var queue = queueOf(host);

            queue.OptimizeThroughput.ShouldBeTrue();
            queue.Parent.EnableDeterministicQueueOrdering.ShouldBeTrue();
            (await columnsAsync(queue.QueueTable)).ShouldContain("seq");
            (await columnsAsync(queue.ScheduledTable)).ShouldContain("seq");
            (await queue.CheckAsync()).ShouldBeTrue();

            var layout = await queue.ResolveLayoutAsync("check", queue.Parent.Settings.ConnectionString!);
            layout.Ready.ShouldBe(new SqlServerTableLayout(true, false));
            layout.Scheduled.ShouldBe(new SqlServerTableLayout(true, false));

            await host.StopAsync(ct);
        }
        finally
        {
            await dropSchemaAsync(schema);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task an_existing_classic_queue_table_is_adopted_as_it_stands(bool hadQueueOrder)
    {
        var schema = $"gh5192_adopt_{Guid.NewGuid():N}";
        await dropSchemaAsync(schema);
        SqlServerNewOnlyHandler.Handled.Clear();

        try
        {
            // A 6.x application: classic tables, with or without its opt-in queue_order, and a backlog
            using (var classic = await startAsync(schema,
                       opts => opts.PublishMessage<SqlServerNewOnlyMessage>().ToSqlServerQueue(QueueName),
                       x =>
                       {
                           x.UseClassicQueueLayout();
                           if (hadQueueOrder) x.EnableDeterministicQueueOrdering();
                           else x.DisableDeterministicQueueOrdering();
                       }, listen: false))
            {
                var bus = classic.MessageBus();
                for (var i = 0; i < 10; i++) await bus.PublishAsync(new SqlServerNewOnlyMessage(i));

                var queue = queueOf(classic);
                await waitUntilAsync(async () => await queue.CountAsync() == 10, "the backlog to reach the queue");

                await classic.StopAsync(ct);
            }

            // The same database after the upgrade, on the 7.0 defaults
            using var upgraded = await startAsync(schema,
                opts => opts.Discovery.IncludeType(typeof(SqlServerNewOnlyHandler)));

            var upgradedQueue = queueOf(upgraded);
            upgradedQueue.OptimizeThroughput.ShouldBeTrue();

            // No rebuild and no new identity, through either the queue or the message store
            (await migrationScriptAsync(upgradedQueue.QueueTable)).ShouldBeEmpty();
            (await migrationScriptAsync(upgradedQueue.ScheduledTable)).ShouldBeEmpty();
            await upgraded.GetRuntime().Storage.Admin.MigrateAsync();
            (await upgradedQueue.CheckAsync()).ShouldBeTrue();

            foreach (var table in new Table[] { upgradedQueue.QueueTable, upgradedQueue.ScheduledTable })
            {
                var columns = await columnsAsync(table);
                columns.ShouldNotContain("seq");
                columns.Contains(QueueTable.QueueOrderColumn).ShouldBe(hadQueueOrder);
            }

            // ...and the listener reads the table with the SQL for the layout it has
            var layout = await upgradedQueue.ResolveLayoutAsync("check", upgradedQueue.Parent.Settings.ConnectionString!);
            layout.Ready.ShouldBe(new SqlServerTableLayout(false, hadQueueOrder));
            layout.Scheduled.ShouldBe(new SqlServerTableLayout(false, hadQueueOrder));

            await waitUntilAsync(() => Task.FromResult(SqlServerNewOnlyHandler.Handled.Count == 10),
                "the backlog to be handled");
            SqlServerNewOnlyHandler.Handled.Keys.OrderBy(x => x).ShouldBe(Enumerable.Range(0, 10));

            await upgraded.StopAsync(ct);
        }
        finally
        {
            await dropSchemaAsync(schema);
        }
    }

    [Fact]
    public async Task an_existing_throughput_table_is_adopted_when_the_classic_defaults_come_back()
    {
        var schema = $"gh5192_back_{Guid.NewGuid():N}";
        await dropSchemaAsync(schema);

        try
        {
            using (var current = await startAsync(schema, _ => { }))
            {
                (await columnsAsync(queueOf(current).QueueTable)).ShouldContain("seq");
                await current.StopAsync(ct);
            }

            // GH-5511. RestoreV6Defaults(): the classic layout as the default, not as an explicit setting
            using var restored = await startAsync(schema, opts => opts.RestoreV6Defaults());
            var queue = queueOf(restored);

            queue.OptimizeThroughput.ShouldBeFalse();
            (await migrationScriptAsync(queue.QueueTable)).ShouldBeEmpty();
            (await columnsAsync(queue.QueueTable)).ShouldContain("seq");
            (await queue.CheckAsync()).ShouldBeTrue();

            var layout = await queue.ResolveLayoutAsync("check", queue.Parent.Settings.ConnectionString!);
            layout.Ready.ShouldBe(new SqlServerTableLayout(true, false));

            await restored.StopAsync(ct);
        }
        finally
        {
            await dropSchemaAsync(schema);
        }
    }

    [Fact]
    public async Task restore_v6_defaults_gives_a_new_queue_table_the_classic_layout()
    {
        // GH-5511. RestoreV6Defaults() comes after the transport is registered, so it is read at startup
        var schema = $"gh5511_new_{Guid.NewGuid():N}";
        await dropSchemaAsync(schema);

        try
        {
            using var host = await startAsync(schema, opts => opts.RestoreV6Defaults());
            var queue = queueOf(host);

            queue.OptimizeThroughput.ShouldBeFalse();
            queue.Parent.EnableDeterministicQueueOrdering.ShouldBeFalse();
            (await columnsAsync(queue.QueueTable)).ShouldNotContain("seq");
            (await columnsAsync(queue.QueueTable)).ShouldNotContain("queue_order");
            (await columnsAsync(queue.ScheduledTable)).ShouldNotContain("seq");
            (await columnsAsync(queue.ScheduledTable)).ShouldNotContain("queue_order");
            (await queue.CheckAsync()).ShouldBeTrue();

            var layout = await queue.ResolveLayoutAsync("check", queue.Parent.Settings.ConnectionString!);
            layout.Ready.ShouldBe(new SqlServerTableLayout(false, false));
            layout.Scheduled.ShouldBe(new SqlServerTableLayout(false, false));

            await host.StopAsync(ct);
        }
        finally
        {
            await dropSchemaAsync(schema);
        }
    }

    [Fact]
    public async Task an_explicit_throughput_layout_wins_over_restore_v6_defaults()
    {
        var schema = $"gh5511_throughput_{Guid.NewGuid():N}";
        await dropSchemaAsync(schema);

        try
        {
            using var host = await startAsync(schema, opts => opts.RestoreV6Defaults(),
                x => x.OptimizeQueueThroughput());
            var queue = queueOf(host);

            queue.OptimizeThroughput.ShouldBeTrue();
            queue.Parent.EnableDeterministicQueueOrdering.ShouldBeFalse();
            (await columnsAsync(queue.QueueTable)).ShouldContain("seq");

            var layout = await queue.ResolveLayoutAsync("check", queue.Parent.Settings.ConnectionString!);
            layout.Ready.ShouldBe(new SqlServerTableLayout(true, false));

            await host.StopAsync(ct);
        }
        finally
        {
            await dropSchemaAsync(schema);
        }
    }

    [Fact]
    public async Task an_explicit_queue_order_wins_over_restore_v6_defaults()
    {
        var schema = $"gh5511_ordered_{Guid.NewGuid():N}";
        await dropSchemaAsync(schema);

        try
        {
            using var host = await startAsync(schema, opts => opts.RestoreV6Defaults(),
                x => x.EnableDeterministicQueueOrdering());
            var queue = queueOf(host);

            queue.OptimizeThroughput.ShouldBeFalse();
            queue.Parent.EnableDeterministicQueueOrdering.ShouldBeTrue();
            (await columnsAsync(queue.QueueTable)).ShouldContain("queue_order");
            (await columnsAsync(queue.QueueTable)).ShouldNotContain("seq");

            var layout = await queue.ResolveLayoutAsync("check", queue.Parent.Settings.ConnectionString!);
            layout.Ready.ShouldBe(new SqlServerTableLayout(false, true));

            await host.StopAsync(ct);
        }
        finally
        {
            await dropSchemaAsync(schema);
        }
    }

    private static async Task<IHost> startAsync(string schema, Action<WolverineOptions> configure,
        Action<SqlServerPersistenceExpression>? transport = null, bool listen = true)
    {
        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery();
                var expression = opts.UseSqlServerPersistenceAndTransport(Servers.SqlServerConnectionString, schema,
                    transportSchema: schema).AutoProvision();
                transport?.Invoke(expression);

                if (listen)
                {
                    opts.ListenToSqlServerQueue(QueueName).PollingInterval(250.Milliseconds());
                }

                configure(opts);
            }).StartAsync(ct);
    }

    private static SqlServerQueue queueOf(IHost host)
    {
        return host.GetRuntime().Options.Transports.OfType<SqlServerTransport>().Single().Queues[QueueName];
    }

    private static async Task<List<string>> columnsAsync(Table table)
    {
        await using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand(
            "select COLUMN_NAME from INFORMATION_SCHEMA.COLUMNS where TABLE_SCHEMA = @schema and TABLE_NAME = @table");
        cmd.With("schema", table.Identifier.Schema);
        cmd.With("table", table.Identifier.Name);

        var columns = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) columns.Add(reader.GetString(0));
        return columns;
    }

    private static async Task<string> migrationScriptAsync(Table table)
    {
        await using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync(ct);
        var migration = await SchemaMigration.DetermineAsync(conn, ct, table);
        using var writer = new StringWriter();
        migration.WriteAllUpdates(writer, new SqlServerMigrator(), AutoCreate.CreateOrUpdate);
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
        await using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync(ct);
        await conn.DropSchemaAsync(schema, ct: ct);
    }
}

public record SqlServerNewOnlyMessage(int Number);

public static class SqlServerNewOnlyHandler
{
    public static readonly ConcurrentDictionary<int, bool> Handled = new();

    public static void Handle(SqlServerNewOnlyMessage message) => Handled[message.Number] = true;
}
