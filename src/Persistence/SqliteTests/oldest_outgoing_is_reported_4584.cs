using JasperFx.Core;
using Wolverine.ComplianceTests;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Wolverine.Sqlite;

namespace SqliteTests;

/// <summary>
/// Follow-up to GH-4499. The SQLite half of <c>PersistedCounts.OldestOutgoing</c>, and the reason this needs
/// a test per provider rather than only unit tests over the health signal: SQLite stores the outgoing
/// <c>timestamp</c> column as <b>TEXT</b>, so the driver hands back a string where Postgres hands back a
/// <c>DateTimeOffset</c>. The parse branch that covers it is only exercised here.
/// </summary>
public class oldest_outgoing_is_reported_4584 : IAsyncLifetime
{
    private readonly SqliteTestDatabase _database = Servers.CreateDatabase("oldest_outgoing_4584");
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Durability.OutboxStaleTime = 5.Minutes();
                opts.PersistMessagesWithSqlite(_database.ConnectionString);
            }).StartAsync();

        await _host.RebuildAllEnvelopeStorageAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
        _database.Dispose();
    }

    [Fact]
    public async Task the_text_timestamp_column_is_parsed_into_the_head()
    {
        var store = _host.GetRuntime().Storage;

        (await store.Admin.FetchCountsAsync()).OldestOutgoing.ShouldBeNull("an empty outbox has no head");

        await store.Outbox.StoreOutgoingAsync(ObjectMother.Envelope(), 0);

        var counts = await store.Admin.FetchCountsAsync();
        counts.Outgoing.ShouldBe(1);

        // The assertion that matters: a string came back and was understood, rather than silently falling
        // through to null and standing the health signal down forever on every SQLite host
        var head = counts.OldestOutgoing.ShouldNotBeNull();
        head.ShouldBeGreaterThan(DateTimeOffset.UtcNow.Subtract(10.Minutes()));
        head.ShouldBeLessThan(DateTimeOffset.UtcNow.Add(10.Minutes()));
    }
}
