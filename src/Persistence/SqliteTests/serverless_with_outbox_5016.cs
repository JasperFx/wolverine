using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Data.Sqlite;
using Wolverine.ComplianceTests;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;
using Wolverine.Runtime;
using Wolverine.Sqlite;
using Wolverine.Tracking;
using Wolverine.Transports.Stub;

namespace SqliteTests;

/// <summary>
/// GH-5016. SQLite under DurabilityMode.ServerlessWithOutbox: outbox rows are owned by any node (0) from
/// the start, a durability pass recovers only the STALE owner-0 rows, deletes what it sent in one batch, and
/// overlapping passes are kept apart by a row in wolverine_locks.
/// </summary>
[Collection("sqlite")]
public class serverless_with_outbox_5016 : IAsyncLifetime
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private readonly SqliteTestDatabase _database = Servers.CreateDatabase(nameof(serverless_with_outbox_5016));

    private IHost theHost = null!;
    private SqliteMessageStore theStore = null!;

    public async ValueTask InitializeAsync()
    {
        theHost = await buildHostAsync(_database.ConnectionString, _ => { });
        theStore = theHost.GetRuntime().Storage.As<SqliteMessageStore>();
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
        _database.Dispose();
    }

    private static async Task<IHost> buildHostAsync(string connectionString, Action<WolverineOptions> configure)
    {
        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.ServerlessWithOutbox;
                opts.Discovery.DisableConventionalDiscovery();
                opts.PersistMessagesWithSqlite(connectionString);
                configure(opts);
            }).StartAsync();

        await host.RebuildAllEnvelopeStorageAsync();

        return host;
    }

    private IReadOnlyList<Envelope> sentTo(string queueName)
    {
        return theHost.GetRuntime().Options.Transports.GetOrCreate<StubTransport>().Endpoints[queueName].Sent
            .ToArray();
    }

    private async Task executeAsync(string sql)
    {
        await using var conn = new SqliteConnection(_database.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<Envelope> storeOutgoingAsync(string destination, int ownerId = 0, bool stale = true)
    {
        var envelope = ObjectMother.Envelope();
        envelope.Destination = destination.ToUri();
        envelope.DeliverBy = null;

        await theStore.StoreOutgoingAsync(envelope, ownerId);

        if (stale)
        {
            await executeAsync(
                $"update {DatabaseConstants.OutgoingTable} set \"timestamp\" = datetime('now', '-2 hours') where lower(id) = '{envelope.Id:D}'");
        }

        return envelope;
    }

    private async Task<Dictionary<Guid, int>> outboxRowsAsync()
    {
        await using var conn = new SqliteConnection(_database.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"select id, owner_id from {DatabaseConstants.OutgoingTable}";

        var rows = new Dictionary<Guid, int>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows[Guid.Parse(reader.GetString(0))] = reader.GetInt32(1);
        }

        return rows;
    }

    private static async Task<bool> hasStaleIndexAsync(string connectionString)
    {
        await using var conn = new SqliteConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "select count(*) from sqlite_master where type = 'index' and name = @name";
        cmd.Parameters.AddWithValue("@name", $"idx_{DatabaseConstants.OutgoingTable}_stale");

        return Convert.ToInt64(await cmd.ExecuteScalarAsync()) > 0;
    }

    [Fact]
    public async Task outgoing_rows_written_in_a_transaction_are_owned_by_any_node()
    {
        var envelopes = new[] { ObjectMother.Envelope(), ObjectMother.Envelope() };
        foreach (var envelope in envelopes) envelope.Destination = "stub://one".ToUri();

        await using (var conn = new SqliteConnection(_database.ConnectionString))
        {
            await conn.OpenAsync(Token);
            await using var tx = await conn.BeginTransactionAsync(Token);
            await theStore.StoreOutgoingAsync(tx, envelopes);
            await tx.CommitAsync(Token);
        }

        var rows = await outboxRowsAsync();
        foreach (var envelope in envelopes)
        {
            rows[envelope.Id].ShouldBe(0);
        }
    }

    [Fact]
    public async Task the_timestamp_column_exists_but_its_index_is_opt_in()
    {
        // The 60 second default OutboxStaleTime under ServerlessWithOutbox brings the column
        theHost.GetRuntime().Options.Durability.OutboxStaleTime.ShouldBe(60.Seconds());
        (await hasStaleIndexAsync(_database.ConnectionString)).ShouldBeFalse();

        using var indexedDatabase = Servers.CreateDatabase("serverless_outbox_indexed");
        using var indexed = await buildHostAsync(indexedDatabase.ConnectionString,
            opts => opts.Durability.EnableOutgoingTimestampIndex = true);

        (await hasStaleIndexAsync(indexedDatabase.ConnectionString)).ShouldBeTrue();

        await indexed.StopAsync(Token);
    }

    [Fact]
    public async Task the_durability_pass_lock_is_exclusive()
    {
        var first = await theStore.TryAcquireDurabilityPassLockAsync(Token);
        first.ShouldNotBeNull();

        try
        {
            (await theStore.TryAcquireDurabilityPassLockAsync(Token)).ShouldBeNull();
        }
        finally
        {
            await first.DisposeAsync();
        }

        var again = await theStore.TryAcquireDurabilityPassLockAsync(Token);
        again.ShouldNotBeNull();
        await again.DisposeAsync();
    }

    [Fact]
    public async Task an_abandoned_durability_pass_lock_is_reaped_after_its_ttl()
    {
        // A pass that died without releasing its lock row
        await executeAsync(
            $"insert into wolverine_locks (lock_id, acquired_at) values ({theStore.DurabilityPassLockId}, datetime('now', '-1 hours'))");

        var taken = await theStore.TryAcquireDurabilityPassLockAsync(Token);
        taken.ShouldNotBeNull();
        await taken.DisposeAsync();
    }

    [Fact]
    public async Task a_pass_that_overruns_the_lock_ttl_keeps_its_lock()
    {
        // GH-5035. A short TTL so the test does not wait out the two minute default. The holder renews its
        // row every quarter of the TTL
        theStore.DurabilityPassLockTtl = 4.Seconds();

        var running = await theStore.TryAcquireDurabilityPassLockAsync(Token);
        running.ShouldNotBeNull();

        try
        {
            // Well past the TTL, so an unrenewed row would be reaped here
            await Task.Delay(7.Seconds(), Token);

            (await theStore.TryAcquireDurabilityPassLockAsync(Token)).ShouldBeNull();
        }
        finally
        {
            await running.DisposeAsync();
        }

        var again = await theStore.TryAcquireDurabilityPassLockAsync(Token);
        again.ShouldNotBeNull();
        await again.DisposeAsync();
    }

    [Fact]
    public async Task a_stale_holder_cannot_release_the_lock_of_the_pass_that_reaped_it()
    {
        // GH-5035
        var stale = await theStore.TryAcquireDurabilityPassLockAsync(Token);
        stale.ShouldNotBeNull();

        // The holder stalled long enough for its row to age past the TTL, so the next pass reaps it
        await executeAsync(
            $"update wolverine_locks set acquired_at = datetime('now', '-1 hours') where lock_id = {theStore.DurabilityPassLockId}");

        var newer = await theStore.TryAcquireDurabilityPassLockAsync(Token);
        newer.ShouldNotBeNull();

        try
        {
            // The stale holder finishing must leave the newer pass's lock in place
            await stale.DisposeAsync();

            (await theStore.TryAcquireDurabilityPassLockAsync(Token)).ShouldBeNull();
        }
        finally
        {
            await newer.DisposeAsync();
        }

        var again = await theStore.TryAcquireDurabilityPassLockAsync(Token);
        again.ShouldNotBeNull();
        await again.DisposeAsync();
    }

    [Fact]
    public async Task a_pass_skips_the_store_while_another_pass_holds_its_lock()
    {
        var envelope = await storeOutgoingAsync("stub://locked");

        await using (await theStore.TryAcquireDurabilityPassLockAsync(Token))
        {
            var report = await theHost.RunDurabilityPassAsync(30.Seconds(), Token);

            report.SkippedStores.ShouldContain(theStore.Uri);
            report.OutgoingSent.ShouldBe(0);
        }

        sentTo("locked").ShouldBeEmpty();
        (await outboxRowsAsync()).Keys.ShouldContain(envelope.Id);
    }

    [Fact]
    public async Task a_pass_sends_only_the_stale_owner_zero_rows_and_deletes_them()
    {
        var stale1 = await storeOutgoingAsync("stub://one");
        var stale2 = await storeOutgoingAsync("stub://one");
        var stale3 = await storeOutgoingAsync("stub://two");

        // Still within OutboxStaleTime: the request that wrote it may still be sending it inline
        var fresh = await storeOutgoingAsync("stub://one", stale: false);

        // Owned by a node, which is not the durability pass's business
        var owned = await storeOutgoingAsync("stub://one", ownerId: 7);

        var report = await theHost.RunDurabilityPassAsync(30.Seconds(), Token);

        report.OutgoingSent.ShouldBe(3);
        report.OutgoingFailed.ShouldBe(0);
        report.Remaining.ShouldBe(0);
        report.Stores.Single(x => x.StoreUri == theStore.Uri).Lock.ShouldBe(DurabilityPassLockStatus.Acquired);

        sentTo("one").Select(x => x.Id).OrderBy(x => x).ShouldBe(new[] { stale1.Id, stale2.Id }.OrderBy(x => x));
        sentTo("two").Single().Id.ShouldBe(stale3.Id);

        var rows = await outboxRowsAsync();
        rows.Keys.OrderBy(x => x).ShouldBe(new[] { fresh.Id, owned.Id }.OrderBy(x => x));
    }

    [Fact]
    public async Task a_pass_works_through_more_rows_than_one_batch()
    {
        for (var i = 0; i < 5; i++)
        {
            await storeOutgoingAsync("stub://batched");
        }

        var report = await theHost.RunDurabilityPassAsync(new DurabilityPassOptions
        {
            MaxDuration = 30.Seconds(),
            BatchSize = 2
        }, Token);

        report.OutgoingSent.ShouldBe(5);
        report.Remaining.ShouldBe(0);
        report.HasMoreWork.ShouldBeFalse();

        var step = report.Stores.Single(x => x.StoreUri == theStore.Uri).Steps
            .Single(x => x.StepName == "Recover stale outgoing messages");
        step.Calls.ShouldBe(3);

        sentTo("batched").Count.ShouldBe(5);
        (await outboxRowsAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task a_row_that_fails_to_send_is_left_in_the_outbox()
    {
        // No transport answers this scheme, so there is no sender for it
        var unsendable = await storeOutgoingAsync("nowhere://lost");
        var good = await storeOutgoingAsync("stub://good");

        var report = await theHost.RunDurabilityPassAsync(30.Seconds(), Token);

        report.OutgoingSent.ShouldBe(1);
        report.OutgoingFailed.ShouldBe(1);
        report.FailedSteps.ShouldBe(0);

        sentTo("good").Single().Id.ShouldBe(good.Id);

        var rows = await outboxRowsAsync();
        rows.Keys.ShouldBe([unsendable.Id]);

        // Still owned by any node, so the next pass tries it again
        rows[unsendable.Id].ShouldBe(0);
    }

    [Fact]
    public async Task an_expired_row_is_discarded_rather_than_sent()
    {
        var envelope = ObjectMother.Envelope();
        envelope.Destination = "stub://expired".ToUri();
        envelope.DeliverBy = DateTimeOffset.UtcNow.Subtract(1.Hours());
        await theStore.StoreOutgoingAsync(envelope, 0);

        await executeAsync(
            $"update {DatabaseConstants.OutgoingTable} set \"timestamp\" = datetime('now', '-2 hours')");

        var report = await theHost.RunDurabilityPassAsync(30.Seconds(), Token);

        report.OutgoingSent.ShouldBe(0);
        report.ExpiredDeleted.ShouldBe(1);
        sentTo("expired").ShouldBeEmpty();
        (await outboxRowsAsync()).ShouldBeEmpty();
    }
}
