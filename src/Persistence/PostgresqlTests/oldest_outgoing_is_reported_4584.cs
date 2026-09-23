using IntegrationTests;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Persistence.Durability;
using Wolverine.Postgresql;
using Wolverine.Runtime;
using Wolverine.RDBMS;
using Wolverine.Tracking;
using Xunit;

namespace PostgresqlTests;

/// <summary>
/// Follow-up to GH-4499. <c>PersistedCounts.OldestOutgoing</c> is the head of the durable outbox, and it is
/// what lets the stuck-outbox health signal tell a wedged outbox from a busy one — a depth reading cannot,
/// because 500 rows look the same whether they are the same 500 every poll or 500 different ones.
/// </summary>
/// <remarks>
/// Worth an integration test per provider rather than only unit tests over the signal: the value is read with
/// hand-written SQL against a column that <em>only exists</em> when <c>OutboxStaleTime</c> is set, and the
/// driver hands the timestamp back as a different CLR type on each engine. Postgres returns
/// <c>DateTimeOffset</c> here; SQLite stores the column as TEXT and returns a string.
/// </remarks>
public class oldest_outgoing_is_reported_4584
{
    private static async Task<IHost> startAsync(TimeSpan? outboxStaleTime)
    {
        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Durability.OutboxStaleTime = outboxStaleTime;
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "oldest_outgoing_4584");
            }).StartAsync();

        await host.RebuildAllEnvelopeStorageAsync();
        return host;
    }

    [Fact]
    public async Task reports_the_head_of_the_outbox_when_the_column_exists()
    {
        using var host = await startAsync(5.Minutes());
        var store = host.GetRuntime().Storage;

        (await store.Admin.FetchCountsAsync()).OldestOutgoing
            .ShouldBeNull("an empty outbox has no head");

        var before = DateTimeOffset.UtcNow.Subtract(5.Seconds());
        await store.Outbox.StoreOutgoingAsync(ObjectMother.Envelope(), 0);
        var after = DateTimeOffset.UtcNow.Add(5.Seconds());

        var counts = await store.Admin.FetchCountsAsync();
        counts.Outgoing.ShouldBe(1);

        var head = counts.OldestOutgoing.ShouldNotBeNull();
        head.ShouldBeGreaterThan(before);
        head.ShouldBeLessThan(after);
    }

    /// <summary>
    /// The head must be the OLDEST row, not the newest and not an arbitrary one — the whole signal rests on
    /// it staying put while newer rows come and go.
    /// </summary>
    [Fact]
    public async Task reports_the_oldest_row_rather_than_the_newest()
    {
        using var host = await startAsync(5.Minutes());
        var store = host.GetRuntime().Storage;

        await store.Outbox.StoreOutgoingAsync(ObjectMother.Envelope(), 0);
        var firstHead = (await store.Admin.FetchCountsAsync()).OldestOutgoing.ShouldNotBeNull();

        // Far enough apart that the two rows cannot share a timestamp
        await Task.Delay(1.Seconds(), TestContext.Current.CancellationToken);
        await store.Outbox.StoreOutgoingAsync(ObjectMother.Envelope(), 0);

        var counts = await store.Admin.FetchCountsAsync();
        counts.Outgoing.ShouldBe(2);

        // Unmoved: a second envelope arriving does not advance the head
        counts.OldestOutgoing.ShouldBe(firstHead);
    }

    /// <summary>
    /// Without <c>OutboxStaleTime</c> the timestamp column is never created, so there is nothing to read and
    /// the value must stay null — the documented NOT MEASURED signal that stands the health check down.
    /// Asserted because reading a column that does not exist would throw rather than return null.
    /// </summary>
    [Fact]
    public async Task reports_nothing_when_the_column_does_not_exist()
    {
        using var host = await startAsync(outboxStaleTime: null);
        var store = host.GetRuntime().Storage;

        await store.Outbox.StoreOutgoingAsync(ObjectMother.Envelope(), 0);

        var counts = await store.Admin.FetchCountsAsync();
        counts.Outgoing.ShouldBe(1);
        counts.OldestOutgoing.ShouldBeNull();
    }
}
