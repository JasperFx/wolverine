using IntegrationTests;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Persistence.Durability;
using Wolverine.Postgresql;
using Wolverine.RDBMS;
using Wolverine.Runtime;
using Wolverine.Tracking;

namespace PostgresqlTests;

/// <summary>
/// Regression coverage for GH-3885. FetchCountsAsync() used pg_class.reltuples estimates for the
/// outgoing and dead letter tables. PostgreSQL's autoanalyze only fires after roughly
/// (50 + 0.1 * reltuples) changed tuples, so a small, quiet table never re-analyzes and its
/// estimate freezes at a stale value forever -- the field report was a dead letter table holding
/// 42 rows that reported 40 on every single sample, never flapping.
/// </summary>
public class fetch_counts_are_exact_for_small_tables : IAsyncLifetime
{
    private const string SchemaName = "gh3885_counts";

    private IHost theHost = null!;
    private IMessageStore theStore = null!;

    public async ValueTask InitializeAsync()
    {
        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts => { opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, SchemaName); })
            .StartAsync();

        await theHost.RebuildAllEnvelopeStorageAsync();

        theStore = theHost.GetRuntime().Storage;
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    private async Task storeDeadLetters(int count)
    {
        var exception = new DivideByZeroException("Kaboom!");

        for (var i = 0; i < count; i++)
        {
            var envelope = ObjectMother.Envelope();
            await theStore.Inbox.StoreIncomingAsync(envelope);
            await theStore.Inbox.MoveToDeadLetterStorageAsync(envelope, exception);
        }
    }

    private async Task storeOutgoing(int count)
    {
        for (var i = 0; i < count; i++)
        {
            await theStore.Outbox.StoreOutgoingAsync(ObjectMother.Envelope(), 3);
        }
    }

    private static async Task executeAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        await conn.CloseAsync();
    }

    [Fact]
    public async Task dead_letter_count_is_exact_when_statistics_were_never_gathered()
    {
        // 42 is the count from the field report in GH-3885
        await storeDeadLetters(42);

        // Deliberately NOT running ANALYZE here. On a freshly created table
        // reltuples is -1, so this is the "never analyzed" arm.
        var counts = await theStore.Admin.FetchCountsAsync();

        counts.DeadLetter.ShouldBe(42);
    }

    [Fact]
    public async Task dead_letter_count_is_exact_when_the_statistics_are_stale()
    {
        await storeDeadLetters(42);

        // Gather statistics while the table holds 42 rows. From here on, autoanalyze will not
        // fire again for a table this small, so reltuples/relpages are frozen at 42 rows'
        // worth -- exactly the stale state the field report was stuck in.
        await executeAsync($"analyze {SchemaName}.{DatabaseConstants.DeadLetterTable}");

        // No vacuum, so the deleted tuples still occupy the same pages and the estimate is
        // unmoved. Before GH-3885 this reported 42 rather than 4.
        await executeAsync(
            $"delete from {SchemaName}.{DatabaseConstants.DeadLetterTable} where ctid in (select ctid from {SchemaName}.{DatabaseConstants.DeadLetterTable} limit 38)");

        var counts = await theStore.Admin.FetchCountsAsync();

        counts.DeadLetter.ShouldBe(4);
    }

    [Fact]
    public async Task outgoing_count_is_exact_when_statistics_were_never_gathered()
    {
        await storeOutgoing(42);

        var counts = await theStore.Admin.FetchCountsAsync();

        counts.Outgoing.ShouldBe(42);
    }

    [Fact]
    public async Task outgoing_count_is_exact_when_the_statistics_are_stale()
    {
        await storeOutgoing(42);

        await executeAsync($"analyze {SchemaName}.{DatabaseConstants.OutgoingTable}");

        await executeAsync(
            $"delete from {SchemaName}.{DatabaseConstants.OutgoingTable} where ctid in (select ctid from {SchemaName}.{DatabaseConstants.OutgoingTable} limit 38)");

        var counts = await theStore.Admin.FetchCountsAsync();

        counts.Outgoing.ShouldBe(4);
    }

    [Fact]
    public async Task zero_counts_on_empty_tables()
    {
        var counts = await theStore.Admin.FetchCountsAsync();

        counts.DeadLetter.ShouldBe(0);
        counts.Outgoing.ShouldBe(0);
    }
}
