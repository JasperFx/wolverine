using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Weasel.Core;
using Wolverine;
using Wolverine.Postgresql;
using Wolverine.RDBMS;
using Wolverine.RDBMS.Durability;
using Wolverine.Runtime;
using Wolverine.Tracking;

namespace PostgresqlTests.Durability;

/// <summary>
/// GH-3971. The orphaned-message sweep used to be three problems at once, all of them only visible at
/// scale: a predicate that could not use an index (<c>owner_id &lt;&gt; 0 and owner_id not in (live)</c>,
/// a full scan of the whole inbox per database on every 5s cycle), an unbounded UPDATE (one node loss
/// rewrote every row it owned in a single statement — ~910,000 rows across the reporter's shards, at
/// ~12 KB a body), and both of those inside the shared recovery transaction, blocking recovery work and
/// competing with live inbox inserts.
///
/// <para>These tests pin the behaviour of the replacement. The <i>performance</i> claim is not something
/// a test can assert, but it rests on plans that were measured directly against this schema:</para>
///
/// <list type="bullet">
/// <item>old <c>not in</c> predicate — Seq Scan, 837 buffers, 3.0 ms</item>
/// <item>plain <c>select distinct owner_id</c> — Seq Scan, 355 buffers, 8.9 ms (worse, which is why
/// <see cref="Wolverine.Postgresql.PostgresqlMessageStore.DistinctOwnerIdsSql"/> is a recursive skip-scan
/// rather than the obvious DISTINCT)</item>
/// <item>skip-scan — 8 buffers, 0.026 ms</item>
/// <item>new bounded update, nothing orphaned — 2 buffers</item>
/// </list>
/// </summary>
public class orphaned_message_sweep_3971 : IAsyncLifetime
{
    private const string SchemaName = "orphan_sweep_3971";
    private IHost _host = null!;
    private IMessageDatabase _database = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // Balanced so the sweep exists at all, but the timers are irrelevant here — every test
                // drives the command directly so there is no polling race to lose.
                opts.Durability.Mode = DurabilityMode.Balanced;
                opts.Durability.DurabilityAgentEnabled = false;

                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, SchemaName);

                // Balanced startup reads wolverine_nodes, so the schema has to exist before the runtime
                // starts rather than being rebuilt after it.
                opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);
            }).StartAsync();

        _database = (IMessageDatabase)_host.GetRuntime().Storage;
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    private async Task<NpgsqlConnection> openAsync()
    {
        var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();
        return conn;
    }

    private async Task givenIncomingOwnedBy(params int[] owners)
    {
        await using var conn = await openAsync();

        foreach (var owner in owners)
        {
            await conn.CreateCommand(
                    $"insert into {SchemaName}.wolverine_incoming_envelopes (id, status, owner_id, body, message_type, received_at) values (:id, 'Incoming', :owner, :body, 'test', 'local://one')")
                .With("id", Guid.NewGuid())
                .With("owner", owner)
                .With("body", new byte[] { 1, 2, 3 })
                .ExecuteNonQueryAsync();
        }
    }

    private async Task<int[]> ownersInIncoming()
    {
        await using var conn = await openAsync();
        return (await conn.CreateCommand($"select owner_id from {SchemaName}.wolverine_incoming_envelopes")
            .FetchListAsync<int>()).OrderBy(x => x).ToArray();
    }

    private Task sweepAsync(int[] activeNodeNumbers, int highWaterMark = 0)
    {
        var command = new ReleaseOrphanedMessagesCommand(_database, _host.GetRuntime().DurabilitySettings,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, activeNodeNumbers, highWaterMark);

        return command.ExecuteAsync(_host.GetRuntime(), CancellationToken.None);
    }

    [Fact]
    public async Task the_distinct_owner_sql_is_valid_and_finds_every_distinct_owner()
    {
        // The skip-scan is a hand-written recursive CTE, so the first thing worth pinning is simply that
        // PostgreSQL accepts it and that it agrees with a plain DISTINCT about the answer.
        await givenIncomingOwnedBy(0, 3, 3, 3, 7, 12, 12);

        var table = _database.DbObjectNameFor(DatabaseConstants.IncomingTable);

        await using var conn = await openAsync();
        var owners = await conn.CreateCommand(_database.DistinctOwnerIdsSql(table)).FetchListAsync<int>(TestContext.Current.CancellationToken);

        owners.OrderBy(x => x).ShouldBe([3, 7, 12]);
    }

    [Fact]
    public async Task the_distinct_owner_sql_copes_with_an_empty_table()
    {
        // The recursive CTE's anchor is a `limit 1` that returns no row at all here. Worth pinning: an
        // anchor that produced a NULL row instead would loop or throw.
        var table = _database.DbObjectNameFor(DatabaseConstants.IncomingTable);

        await using var conn = await openAsync();
        var owners = await conn.CreateCommand(_database.DistinctOwnerIdsSql(table)).FetchListAsync<int>(TestContext.Current.CancellationToken);

        owners.ShouldBeEmpty();
    }

    [Fact]
    public async Task releases_only_the_rows_owned_by_departed_nodes()
    {
        await givenIncomingOwnedBy(1, 1, 2, 5, 9);

        // Nodes 1 and 2 are live; 5 and 9 have departed.
        await sweepAsync([1, 2]);

        // The two orphans are released to owner_id = 0 for recovery; the live nodes' in-flight work is
        // untouched, which is the half that must never break.
        (await ownersInIncoming()).ShouldBe([0, 0, 1, 1, 2]);
    }

    [Fact]
    public async Task leaves_everything_alone_when_no_owner_has_departed()
    {
        await givenIncomingOwnedBy(1, 2, 3);

        await sweepAsync([1, 2, 3]);

        (await ownersInIncoming()).ShouldBe([1, 2, 3]);
    }

    [Fact]
    public async Task an_unknown_live_node_list_releases_nothing()
    {
        await givenIncomingOwnedBy(1, 2, 3);

        // An empty active-node list means the lookup against the main database failed or found nothing.
        // That is NOT evidence that every owner is dead — treating it as such would reset the entire
        // inbox of every shard at once.
        await sweepAsync([]);

        (await ownersInIncoming()).ShouldBe([1, 2, 3]);
    }

    [Fact]
    public async Task the_high_water_mark_protects_a_node_newer_than_the_cached_list()
    {
        await givenIncomingOwnedBy(1, 2, 4);

        // GH-3850: node 4 registered after the cached list was taken and is already writing. Releasing
        // its rows would hand its in-flight work to somebody else.
        await sweepAsync([1, 2], highWaterMark: 2);

        (await ownersInIncoming()).ShouldBe([1, 2, 4]);
    }

    [Fact]
    public async Task a_departed_high_numbered_node_is_still_reclaimed()
    {
        await givenIncomingOwnedBy(1, 2, 3);

        // The mark is monotonic — it remembers node 3 even though 3 has left the active list — so the
        // highest-numbered node dying does not put its messages permanently out of reach.
        await sweepAsync([1, 2], highWaterMark: 3);

        (await ownersInIncoming()).ShouldBe([0, 1, 2]);
    }

    [Fact]
    public async Task releases_across_more_rows_than_one_batch()
    {
        // Bounded batching is the point of the change, so prove the loop actually drains rather than
        // releasing one batch and calling it done.
        _host.GetRuntime().DurabilitySettings.OrphanedMessageReleaseBatchSize = 10;

        await givenIncomingOwnedBy(Enumerable.Repeat(6, 55).ToArray());

        await sweepAsync([1]);

        (await ownersInIncoming()).ShouldAllBe(x => x == 0);
        (await ownersInIncoming()).Length.ShouldBe(55);
    }

    [Fact]
    public async Task the_per_cycle_batch_cap_is_honoured()
    {
        // The safety valve: a cycle stops after MaxBatchesPerCycle and leaves the rest for the next one,
        // so one node loss cannot monopolise the database.
        var settings = _host.GetRuntime().DurabilitySettings;
        settings.OrphanedMessageReleaseBatchSize = 10;
        settings.OrphanedMessageReleaseMaxBatchesPerCycle = 2;

        await givenIncomingOwnedBy(Enumerable.Repeat(6, 55).ToArray());

        await sweepAsync([1]);

        (await ownersInIncoming()).Count(x => x == 0).ShouldBe(20);
    }

    [Fact]
    public async Task the_sweep_timer_is_actually_wired_up()
    {
        // Everything above drives the command directly, which proves the SQL but not the plumbing --
        // and the plumbing is what GH-3971 changed most: the sweep moved off the shared recovery batch
        // onto its own timer. Run a real host with the durability agent enabled and let the timer fire.
        var schema = "orphan_sweep_timer_3971";

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Balanced;
                opts.Durability.OrphanedMessageSweepPollingTime = 250.Milliseconds();

                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, schema);
                opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);
            }).StartAsync(TestContext.Current.CancellationToken);

        // Node 999 never existed in this cluster, so it is unambiguously departed.
        await using (var conn = await openAsync())
        {
            await conn.CreateCommand(
                    $"insert into {schema}.wolverine_incoming_envelopes (id, status, owner_id, body, message_type, received_at) values (:id, 'Incoming', 999, :body, 'test', 'local://one')")
                .With("id", Guid.NewGuid())
                .With("body", new byte[] { 1, 2, 3 })
                .ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await using var conn = await openAsync();
            var owners = await conn
                .CreateCommand($"select owner_id from {schema}.wolverine_incoming_envelopes")
                .FetchListAsync<int>(TestContext.Current.CancellationToken);

            if (owners.Count > 0 && owners.All(x => x == 0))
            {
                await host.StopAsync(TestContext.Current.CancellationToken);
                return;
            }

            await Task.Delay(250.Milliseconds(), TestContext.Current.CancellationToken);
        }

        await host.StopAsync(TestContext.Current.CancellationToken);
        throw new TimeoutException(
            "The orphaned message sweep timer never released the envelope owned by departed node 999.");
    }
}
