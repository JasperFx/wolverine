using IntegrationTests;
using JasperFx.Core;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Weasel.SqlServer;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.SqlServer;
using Wolverine.SqlServer.Transport;
using Wolverine.Tracking;

namespace SqlServerTests.Transport;

/// <summary>
///     GH-4989. Turning <c>OptimizeQueueThroughput()</c> on or off for queue tables that already exist, and
///     hold messages, used to fail the schema migration in both directions: "column 'seq' cannot be added
///     to an existing table" one way, "'pkey_wolverine_queue_..._' is not a constraint" the other.
/// </summary>
public class switching_queue_table_layouts
{
    private const string QueueName = "switching";
    private static CancellationToken ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task an_existing_queue_switches_layout_and_keeps_its_messages(bool fromOptimized,
        bool deterministicOrdering)
    {
        var schema = $"layout_switch_{Guid.NewGuid():N}";
        try
        {
            List<Envelope> sent;
            List<Envelope> scheduled;

            using (var before = await startAsync(schema, fromOptimized, deterministicOrdering))
            {
                var queue = queueOf(before);
                sent = await sendAsync(queue, 3);
                scheduled = await scheduleAsync(queue, 2);

                // Without EnableDeterministicQueueOrdering() rows that share a timestamp have no defined
                // order (GH-4862), and SYSDATETIMEOFFSET() can give consecutive sends the same one. Make the
                // send order unambiguous so the test is about the rebuild keeping it
                if (!deterministicOrdering) await spreadTimestampsAsync(queue, sent);

                await before.StopAsync(ct);
            }

            using (var after = await startAsync(schema, !fromOptimized, deterministicOrdering))
            {
                var queue = queueOf(after);
                (await queue.CheckAsync()).ShouldBeTrue();

                await assertLayoutAsync(queue.QueueTable.Identifier.QualifiedName, optimized: !fromOptimized);
                await assertLayoutAsync(queue.ScheduledTable.Identifier.QualifiedName, optimized: !fromOptimized);

                (await queue.CountAsync()).ShouldBe(3);
                (await queue.ScheduledCountAsync()).ShouldBe(2);

                var popped = await queue.TryPopDurablyAsync(3, after.GetRuntime().DurabilitySettings,
                    NullLogger.Instance, ct);
                popped.Select(x => x.Id).ShouldBe(sent.Select(x => x.Id));

                await after.StopAsync(ct);
            }

            // ...and the switched schema needs nothing more on the next start
            using (var again = await startAsync(schema, !fromOptimized, deterministicOrdering))
            {
                (await queueOf(again).CheckAsync()).ShouldBeTrue();
                (await queueOf(again).ScheduledCountAsync()).ShouldBe(scheduled.Count);
                await again.StopAsync(ct);
            }
        }
        finally
        {
            await using var conn = new SqlConnection(Servers.SqlServerConnectionString);
            await conn.OpenAsync(ct);
            await conn.DropSchemaAsync(schema, ct);
        }
    }

    private static SqlServerQueue queueOf(IHost host) =>
        host.GetRuntime().Options.Transports.OfType<SqlServerTransport>().Single().Queues[QueueName];

    private static async Task<IHost> startAsync(string schema, bool optimizeThroughput, bool deterministicOrdering)
    {
        await using (var conn = new SqlConnection(Servers.SqlServerConnectionString))
        {
            await conn.OpenAsync(ct);
            await conn.CreateCommand($"IF SCHEMA_ID('{schema}') IS NULL EXEC('CREATE SCHEMA [{schema}]')")
                .ExecuteNonQueryAsync(ct);
        }

        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.ScheduledJobFirstExecution = 1.Hours();
                opts.Durability.ScheduledJobPollingTime = 1.Hours();

                var expression = opts.UseSqlServerPersistenceAndTransport(Servers.SqlServerConnectionString,
                    schema, transportSchema: schema).AutoProvision();
                if (optimizeThroughput) expression.OptimizeQueueThroughput();
                if (deterministicOrdering) expression.EnableDeterministicQueueOrdering();

                opts.ListenToSqlServerQueue(QueueName).PollingInterval(1.Hours());
            }).StartAsync(ct);

        // Nothing but the test takes messages off the queue
        await host.GetRuntime().Endpoints.StopListenerAsync(queueOf(host), ct);
        return host;
    }

    private static async Task<List<Envelope>> sendAsync(SqlServerQueue queue, int count)
    {
        var envelopes = new List<Envelope>();
        for (var i = 0; i < count; i++)
        {
            var envelope = ObjectMother.Envelope();
            envelopes.Add(envelope);
            await queue.SendAsync(envelope, ct);
        }

        return envelopes;
    }

    private static async Task spreadTimestampsAsync(SqlServerQueue queue, List<Envelope> sent)
    {
        await using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync(ct);

        for (var i = 0; i < sent.Count; i++)
        {
            await conn.CreateCommand(
                    $"UPDATE {queue.QueueTable.Identifier} SET timestamp = DATEADD(second, @offset, CAST('2026-01-01T00:00:00+00:00' AS datetimeoffset)) WHERE id = @id")
                .With("offset", i)
                .With("id", sent[i].Id)
                .ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task<List<Envelope>> scheduleAsync(SqlServerQueue queue, int count)
    {
        var envelopes = new List<Envelope>();
        for (var i = 0; i < count; i++)
        {
            var envelope = ObjectMother.Envelope();
            envelope.ScheduledTime = DateTimeOffset.UtcNow.AddDays(1);
            envelopes.Add(envelope);
            await queue.ScheduleMessageAsync(envelope, ct);
        }

        return envelopes;
    }

    private static async Task assertLayoutAsync(string qualifiedName, bool optimized)
    {
        await using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync(ct);

        var seq = await conn.CreateCommand(
                "SELECT count(*) FROM sys.columns WHERE object_id = OBJECT_ID(@table) AND name = 'seq'")
            .With("table", qualifiedName)
            .ExecuteScalarAsync(ct);
        Convert.ToInt32(seq).ShouldBe(optimized ? 1 : 0);

        var primaryKey = await conn.CreateCommand(
                "SELECT count(*) FROM sys.key_constraints WHERE type = 'PK' AND parent_object_id = OBJECT_ID(@table)")
            .With("table", qualifiedName)
            .ExecuteScalarAsync(ct);
        Convert.ToInt32(primaryKey).ShouldBe(optimized ? 0 : 1);

        var clustered = await conn.CreateCommand(
                "SELECT count(*) FROM sys.indexes WHERE type = 1 AND object_id = OBJECT_ID(@table)")
            .With("table", qualifiedName)
            .ExecuteScalarAsync(ct);
        Convert.ToInt32(clustered).ShouldBe(1);
    }
}
