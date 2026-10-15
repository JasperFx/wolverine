using IntegrationTests;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Weasel.Core;
using Weasel.Postgresql;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Persistence.Durability;
using Wolverine.Postgresql;
using Wolverine.RDBMS;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Wolverine.Transports.Stub;

namespace PostgresqlTests;

/// <summary>
/// GH-5016. PostgreSQL under DurabilityMode.ServerlessWithOutbox: outbox rows are owned by any node (0) from
/// the start, a durability pass recovers only the STALE owner-0 rows, deletes what it sent in one batch, and
/// overlapping passes are kept apart by an advisory lock.
/// </summary>
public class serverless_with_outbox_5016 : IAsyncLifetime
{
    private const string SchemaName = "serverless_outbox";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private IHost theHost = null!;
    private PostgresqlMessageStore theStore = null!;

    public async ValueTask InitializeAsync()
    {
        theHost = await buildHostAsync(SchemaName, _ => { });
        theStore = theHost.GetRuntime().Storage.As<PostgresqlMessageStore>();
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    private static async Task<IHost> buildHostAsync(string schemaName, Action<WolverineOptions> configure)
    {
        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.ServerlessWithOutbox;
                opts.Discovery.DisableConventionalDiscovery();
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, schemaName);
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

    private async Task<Envelope> storeOutgoingAsync(string destination, int ownerId = 0, bool stale = true)
    {
        var envelope = ObjectMother.Envelope();
        envelope.Destination = destination.ToUri();
        envelope.DeliverBy = null;

        await theStore.StoreOutgoingAsync(envelope, ownerId);

        if (stale)
        {
            await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
            await conn.OpenAsync();
            await conn.CreateCommand(
                    $"update {SchemaName}.{DatabaseConstants.OutgoingTable} set \"timestamp\" = :time where id = :id")
                .With("time", DateTimeOffset.UtcNow.Subtract(2.Hours()))
                .With("id", envelope.Id)
                .ExecuteNonQueryAsync();
        }

        return envelope;
    }

    private async Task<Dictionary<Guid, int>> outboxRowsAsync()
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();

        var rows = new Dictionary<Guid, int>();
        await using var reader = await conn
            .CreateCommand($"select id, owner_id from {SchemaName}.{DatabaseConstants.OutgoingTable}")
            .ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            rows[await reader.GetFieldValueAsync<Guid>(0)] = await reader.GetFieldValueAsync<int>(1);
        }

        return rows;
    }

    private static async Task<bool> hasStaleIndexAsync(string schemaName)
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();

        var count = await conn
            .CreateCommand("select count(*) from pg_indexes where schemaname = :schema and indexname = :name")
            .With("schema", schemaName)
            .With("name", $"idx_{DatabaseConstants.OutgoingTable}_stale")
            .ExecuteScalarAsync();

        return Convert.ToInt64(count) > 0;
    }

    [Fact]
    public async Task outgoing_rows_written_in_a_transaction_are_owned_by_any_node()
    {
        var envelopes = new[] { ObjectMother.Envelope(), ObjectMother.Envelope() };
        foreach (var envelope in envelopes) envelope.Destination = "stub://one".ToUri();

        await using (var conn = await theStore.NpgsqlDataSource.OpenConnectionAsync(Token))
        {
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
        (await hasStaleIndexAsync(SchemaName)).ShouldBeFalse();

        using var indexed = await buildHostAsync("serverless_outbox_indexed",
            opts => opts.Durability.EnableOutgoingTimestampIndex = true);

        (await hasStaleIndexAsync("serverless_outbox_indexed")).ShouldBeTrue();

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
        var envelopes = new List<Envelope>();
        for (var i = 0; i < 5; i++)
        {
            envelopes.Add(await storeOutgoingAsync("stub://batched"));
        }

        var report = await theHost.RunDurabilityPassAsync(new DurabilityPassOptions
        {
            MaxDuration = 30.Seconds(),
            BatchSize = 2
        }, Token);

        report.OutgoingSent.ShouldBe(5);
        report.Remaining.ShouldBe(0);
        report.HasMoreWork.ShouldBeFalse();

        var step = report.Stores.Single(x => x.StoreUri == theStore.Uri).Steps.Single();
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

        await using (var conn = new NpgsqlConnection(Servers.PostgresConnectionString))
        {
            await conn.OpenAsync(Token);
            await conn.CreateCommand(
                    $"update {SchemaName}.{DatabaseConstants.OutgoingTable} set \"timestamp\" = :time")
                .With("time", DateTimeOffset.UtcNow.Subtract(2.Hours()))
                .ExecuteNonQueryAsync(Token);
        }

        var report = await theHost.RunDurabilityPassAsync(30.Seconds(), Token);

        report.OutgoingSent.ShouldBe(0);
        report.ExpiredDeleted.ShouldBe(1);
        sentTo("expired").ShouldBeEmpty();
        (await outboxRowsAsync()).ShouldBeEmpty();
    }
}
