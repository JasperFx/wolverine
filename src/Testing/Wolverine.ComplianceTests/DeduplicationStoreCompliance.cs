using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;
using Wolverine.RDBMS.Deduplication;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.ComplianceTests;

/// <summary>
/// GH-4757. Compliance facts for <see cref="IDeduplicationStore" /> under both
/// <see cref="MessageDeduplicationMode" />s. Every RDBMS-backed message store inherits these by
/// subclassing and supplying its persistence configuration.
///
/// <para>
/// These live here rather than in one provider's suite because the defect being fixed was <b>a
/// difference between providers</b>. <c>wolverine_deduplication.deduplication_id</c> carried the
/// primary key, so "are these two ids the same claim?" was answered by the column's collation:
/// PostgreSQL and SQLite compare bytes, SQL Server's default collation is case-insensitive, and
/// MySQL's is case- AND accent-insensitive. A suite that only ever ran on PostgreSQL could not see
/// that, and did not.
/// </para>
///
/// <para>
/// The two modes use two separate tables, so these facts also pin the thing that buys: switching mode
/// in either direction provisions the other table and leaves the first one's rows exactly where they
/// were. There is no reshaping migration to get wrong and no window in which a live claim is dropped.
/// </para>
/// </summary>
public abstract class DeduplicationStoreCompliance : IAsyncLifetime
{
    private readonly List<IHost> _hosts = [];

    /// <summary>Wire this provider's message persistence into the options.</summary>
    protected abstract void configurePersistence(WolverineOptions opts);

    /// <summary>
    /// The rendered name of the <see cref="MessageDeduplicationMode.CompareByString" /> table
    /// (<c>wolverine_deduplication</c>) for the schema <see cref="configurePersistence" /> uses —
    /// schema-qualified on engines with schemas, prefixed on SQLite (GH-3943).
    /// </summary>
    protected abstract string deduplicationTableName { get; }

    /// <summary>
    /// The rendered name of the <see cref="MessageDeduplicationMode.CompareByHash" /> table
    /// (<c>wolverine_deduplication_hashed</c>), on the same terms as
    /// <see cref="deduplicationTableName" />.
    /// </summary>
    protected abstract string hashedDeduplicationTableName { get; }

    /// <summary>
    /// Does this engine's default collation compare <c>deduplication_id</c> byte for byte? True on
    /// PostgreSQL and SQLite, false on SQL Server and MySQL.
    ///
    /// <para>
    /// This is the one expectation that is legitimately per-provider, and only in
    /// <see cref="MessageDeduplicationMode.CompareByString" /> — which exists precisely to preserve
    /// that behaviour rather than to fix it. Under
    /// <see cref="MessageDeduplicationMode.CompareByHash" /> every provider must answer identically,
    /// and the facts below assert that without consulting this.
    /// </para>
    /// </summary>
    protected abstract bool stringComparisonIsCaseSensitive { get; }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var host in _hosts)
        {
            try
            {
                await host.StopAsync();
            }
            finally
            {
                host.Dispose();
            }
        }
    }

    private async Task<IHost> startHostAsync(MessageDeduplicationMode mode, bool rebuild = true)
    {
        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery();
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Durability.MessageDeduplicationMode = mode;
                opts.Durability.DeduplicationWindow = 1.Hours();

                configurePersistence(opts);
            }).StartAsync(TestContext.Current.CancellationToken);

        _hosts.Add(host);

        // Drop and recreate rather than migrate: each fact wants a known-empty table. The two facts
        // that care about what a mode switch does to the OTHER mode's table pass rebuild: false and
        // drive the migration themselves.
        if (rebuild) await host.RebuildAllEnvelopeStorageAsync();

        return host;
    }

    private static IDeduplicationStore storeFor(IHost host) => host.GetRuntime().Storage.Deduplication;

    private static DateTimeOffset anHourFromNow => DateTimeOffset.UtcNow.Add(1.Hours());

    /// <summary>
    /// The column names on <paramref name="table" />, read off a zero-row reader so this works the same
    /// on all four engines without a per-provider schema API.
    /// </summary>
    private async Task<string[]> columnsAsync(IHost host, string table)
    {
        var database = (IMessageDatabase)host.GetRuntime().Storage;

        await using var cmd = database.DataSource.CreateCommand($"select * from {table} where 1 = 0");
        await using var reader = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);

        return Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
    }

    /// <summary>
    /// The table names this host's mode actually declares, via the same <c>AllObjects()</c> the
    /// migration runs off.
    ///
    /// <para>
    /// Asserted on in preference to "does this table exist in the database?", which is a different and
    /// misleading question: a mode switch deliberately LEAVES the other mode's table in place, so any
    /// schema that has run both modes has both tables sitting in it. What the provisioning contract
    /// actually says is which one this mode asks for.
    /// </para>
    /// </summary>
    private static string[] declaredObjectNamesFor(IHost host)
    {
        return ((Weasel.Core.Migrations.IDatabase)host.GetRuntime().Storage).AllObjects()
            .Select(x => x.Identifier.Name).ToArray();
    }

    /// <summary>
    /// Every readable id in <paramref name="table" />, in no particular order.
    /// </summary>
    private async Task<string[]> storedIdsAsync(IHost host, string table)
    {
        var database = (IMessageDatabase)host.GetRuntime().Storage;

        await using var cmd = database.DataSource.CreateCommand(
            $"select {DatabaseConstants.DeduplicationId} from {table}");
        await using var reader = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);

        var found = new List<string>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            found.Add(await reader.GetFieldValueAsync<string>(0, TestContext.Current.CancellationToken));
        }

        return found.ToArray();
    }

    // ---------------------------------------------------------------------------------------------
    // CompareByHash -- the fix
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// GH-4757, the headline defect. @uniquelau's report: on SQL Server and MySQL these were ONE claim,
    /// so the second request was refused as a duplicate of work that had never been done.
    /// </summary>
    [Fact]
    public async Task ids_differing_only_in_case_are_distinct_claims_under_hash_comparison()
    {
        var store = storeFor(await startHostAsync(MessageDeduplicationMode.CompareByHash));

        (await store.TryClaimAsync("Abc", anHourFromNow, TestContext.Current.CancellationToken))
            .ShouldBeTrue();

        (await store.TryClaimAsync("abc", anHourFromNow, TestContext.Current.CancellationToken))
            .ShouldBeTrue("'abc' is a different logical id from 'Abc' and must not be refused as a duplicate");
    }

    /// <summary>
    /// The MySQL half of the same report: its default collation is accent-insensitive as well as
    /// case-insensitive.
    /// </summary>
    [Fact]
    public async Task ids_differing_only_in_accent_are_distinct_claims_under_hash_comparison()
    {
        var store = storeFor(await startHostAsync(MessageDeduplicationMode.CompareByHash));

        (await store.TryClaimAsync("José", anHourFromNow, TestContext.Current.CancellationToken))
            .ShouldBeTrue();

        (await store.TryClaimAsync("Jose", anHourFromNow, TestContext.Current.CancellationToken))
            .ShouldBeTrue("'Jose' is a different logical id from 'José' and must not be refused as a duplicate");
    }

    /// <summary>
    /// The other side of the same coin, and the thing that would break if the hash were computed over
    /// anything but the id: an exact repeat is still one claim.
    /// </summary>
    [Fact]
    public async Task an_exact_repeat_is_still_refused_under_hash_comparison()
    {
        var store = storeFor(await startHostAsync(MessageDeduplicationMode.CompareByHash));

        (await store.TryClaimAsync("schedule-1|2026-10-01T03:00:00Z", anHourFromNow,
            TestContext.Current.CancellationToken)).ShouldBeTrue();

        (await store.TryClaimAsync("schedule-1|2026-10-01T03:00:00Z", anHourFromNow,
            TestContext.Current.CancellationToken)).ShouldBeFalse();
    }

    /// <summary>
    /// Why <c>deduplication_id</c> survives at all once it stops being the key: an operator looking at a
    /// stuck claim has to be able to tell WHICH id it is, and a 32-byte hash cannot be reversed.
    /// </summary>
    [Fact]
    public async Task the_readable_id_is_stored_alongside_the_hash()
    {
        var host = await startHostAsync(MessageDeduplicationMode.CompareByHash);

        await storeFor(host).TryClaimAsync("Invoice-17|José", anHourFromNow,
            TestContext.Current.CancellationToken);

        (await storedIdsAsync(host, hashedDeduplicationTableName)).ShouldBe(["Invoice-17|José"]);
    }

    [Fact]
    public async Task releasing_a_claim_lets_the_same_id_be_claimed_again_under_hash_comparison()
    {
        var store = storeFor(await startHostAsync(MessageDeduplicationMode.CompareByHash));

        await store.TryClaimAsync("released", anHourFromNow, TestContext.Current.CancellationToken);
        await store.ReleaseAsync("released", TestContext.Current.CancellationToken);

        (await store.TryClaimAsync("released", anHourFromNow, TestContext.Current.CancellationToken))
            .ShouldBeTrue();
    }

    /// <summary>
    /// The release has to key off the SAME column the claim did, or it silently deletes nothing and the
    /// compensating release in <c>MessageDeduplicator</c> poisons the id until it expires.
    /// </summary>
    [Fact]
    public async Task releasing_one_id_does_not_release_a_case_variant_of_it_under_hash_comparison()
    {
        var store = storeFor(await startHostAsync(MessageDeduplicationMode.CompareByHash));

        await store.TryClaimAsync("Abc", anHourFromNow, TestContext.Current.CancellationToken);
        await store.TryClaimAsync("abc", anHourFromNow, TestContext.Current.CancellationToken);

        await store.ReleaseAsync("abc", TestContext.Current.CancellationToken);

        (await store.TryClaimAsync("abc", anHourFromNow, TestContext.Current.CancellationToken))
            .ShouldBeTrue();
        (await store.TryClaimAsync("Abc", anHourFromNow, TestContext.Current.CancellationToken))
            .ShouldBeFalse("releasing 'abc' must leave 'Abc' claimed");
    }

    [Fact]
    public async Task the_reaper_deletes_expired_claims_under_hash_comparison()
    {
        var store = storeFor(await startHostAsync(MessageDeduplicationMode.CompareByHash));

        await store.TryClaimAsync("expired-1", DateTimeOffset.UtcNow.Subtract(1.Hours()),
            TestContext.Current.CancellationToken);
        await store.TryClaimAsync("expired-2", DateTimeOffset.UtcNow.Subtract(1.Hours()),
            TestContext.Current.CancellationToken);
        await store.TryClaimAsync("still-live", anHourFromNow, TestContext.Current.CancellationToken);

        (await store.DeleteExpiredAsync(DateTimeOffset.UtcNow, TestContext.Current.CancellationToken))
            .ShouldBe(2);

        (await store.TryClaimAsync("still-live", anHourFromNow, TestContext.Current.CancellationToken))
            .ShouldBeFalse();
        (await store.TryClaimAsync("expired-1", anHourFromNow, TestContext.Current.CancellationToken))
            .ShouldBeTrue();
    }

    /// <summary>
    /// The race the feature exists for, re-asserted against the moved key. A SELECT-then-INSERT
    /// implementation — or a hash column with no unique constraint on it — passes every other fact here
    /// and fails only this one.
    /// </summary>
    [Fact]
    public async Task concurrent_claims_of_one_id_produce_exactly_one_winner_under_hash_comparison()
    {
        var store = storeFor(await startHostAsync(MessageDeduplicationMode.CompareByHash));

        var expires = anHourFromNow;

        var results = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => store.TryClaimAsync("contended", expires, TestContext.Current.CancellationToken)));

        results.Count(x => x).ShouldBe(1);
    }

    /// <summary>
    /// Hash mode provisions its OWN table and leaves <c>wolverine_deduplication</c> alone — which is the
    /// whole reason the upgrade is non-destructive, so it is asserted rather than assumed.
    /// </summary>
    [Fact]
    public async Task hash_comparison_provisions_only_the_hashed_table()
    {
        var host = await startHostAsync(MessageDeduplicationMode.CompareByHash);

        var columns = await columnsAsync(host, hashedDeduplicationTableName);

        columns.ShouldContain(DatabaseConstants.DeduplicationHash);
        columns.ShouldContain(DatabaseConstants.DeduplicationId);
        columns.ShouldContain(DatabaseConstants.Expires);

        // Declared, rather than "present in the database": the original table is deliberately left
        // alone by this mode, so on any schema that has run both it is still there.
        var declared = declaredObjectNamesFor(host);
        declared.ShouldContain(x => x.EndsWith(DatabaseConstants.HashedDeduplicationTableName));
        declared.ShouldNotContain(x => x.EndsWith(DatabaseConstants.DeduplicationTableName));
    }

    /// <summary>
    /// The hash column is BINARY, not text. Everything else about this mode is correct and inert if it
    /// is not: a text column would be compared under the collation again, and only on the two engines
    /// where that differs, which is exactly how GH-4757 went unnoticed.
    /// </summary>
    [Fact]
    public async Task the_hash_column_is_binary()
    {
        var host = await startHostAsync(MessageDeduplicationMode.CompareByHash);

        var database = (IMessageDatabase)host.GetRuntime().Storage;

        await using var cmd = database.DataSource.CreateCommand(
            $"select {DatabaseConstants.DeduplicationHash} from {hashedDeduplicationTableName} where 1 = 0");
        await using var reader = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);

        reader.GetFieldType(0).ShouldBe(typeof(byte[]));
    }

    // ---------------------------------------------------------------------------------------------
    // CompareByString -- the escape hatch, which has to preserve the old behaviour rather than
    // quietly get the new one
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The original shape, plus only the nullable envelope column D1 added -- an additive migration that leaves the
    /// key, and therefore the comparison, exactly as it was.
    /// </summary>
    [Fact]
    public async Task string_comparison_provisions_only_the_original_shape_plus_the_envelope_column()
    {
        var host = await startHostAsync(MessageDeduplicationMode.CompareByString);

        (await columnsAsync(host, deduplicationTableName))
            .ShouldBe([DatabaseConstants.DeduplicationId, DatabaseConstants.Expires, DatabaseConstants.DeduplicationEnvelopeId],
                ignoreOrder: true);

        var declared = declaredObjectNamesFor(host);
        declared.ShouldContain(x => x.EndsWith(DatabaseConstants.DeduplicationTableName));
        declared.ShouldNotContain(x => x.EndsWith(DatabaseConstants.HashedDeduplicationTableName));
    }

    /// <summary>
    /// The assertion that proves <see cref="MessageDeduplicationMode.CompareByString" /> really is the
    /// old behaviour and not the new one wearing its name: on a case-insensitive engine the two ids
    /// still collide. That is a defect, and it is the defect this mode exists to keep for anyone who
    /// cannot take the schema change.
    /// </summary>
    [Fact]
    public async Task string_comparison_keeps_this_engines_collation_behaviour()
    {
        var store = storeFor(await startHostAsync(MessageDeduplicationMode.CompareByString));

        (await store.TryClaimAsync("Abc", anHourFromNow, TestContext.Current.CancellationToken))
            .ShouldBeTrue();

        (await store.TryClaimAsync("abc", anHourFromNow, TestContext.Current.CancellationToken))
            .ShouldBe(stringComparisonIsCaseSensitive);
    }

    /// <summary>
    /// GH-5206. An HTTP endpoint whose claim rides its own transaction looks a claim up again, without claiming it,
    /// when a concurrent repeat fails before its commit.
    /// </summary>
    [Theory]
    [InlineData(MessageDeduplicationMode.CompareByHash)]
    [InlineData(MessageDeduplicationMode.CompareByString)]
    public async Task a_claim_can_be_looked_up_without_taking_it(MessageDeduplicationMode mode)
    {
        var store = storeFor(await startHostAsync(mode));
        var lookup = store.ShouldBeAssignableTo<IDeduplicationClaimLookup>()!;

        (await lookup.IsClaimedAsync("looked-up", TestContext.Current.CancellationToken)).ShouldBeFalse();

        // The lookup took nothing
        (await store.TryClaimAsync("looked-up", anHourFromNow, TestContext.Current.CancellationToken)).ShouldBeTrue();
        (await lookup.IsClaimedAsync("looked-up", TestContext.Current.CancellationToken)).ShouldBeTrue();
        (await lookup.IsClaimedAsync("something-else", TestContext.Current.CancellationToken)).ShouldBeFalse();

        await store.ReleaseAsync("looked-up", TestContext.Current.CancellationToken);
        (await lookup.IsClaimedAsync("looked-up", TestContext.Current.CancellationToken)).ShouldBeFalse();
    }

    [Fact]
    public async Task string_comparison_still_refuses_an_exact_repeat()
    {
        var store = storeFor(await startHostAsync(MessageDeduplicationMode.CompareByString));

        (await store.TryClaimAsync("schedule-1|2026-10-01T03:00:00Z", anHourFromNow,
            TestContext.Current.CancellationToken)).ShouldBeTrue();

        (await store.TryClaimAsync("schedule-1|2026-10-01T03:00:00Z", anHourFromNow,
            TestContext.Current.CancellationToken)).ShouldBeFalse();
    }

    [Fact]
    public async Task string_comparison_still_releases_and_reaps()
    {
        var store = storeFor(await startHostAsync(MessageDeduplicationMode.CompareByString));

        await store.TryClaimAsync("released", anHourFromNow, TestContext.Current.CancellationToken);
        await store.ReleaseAsync("released", TestContext.Current.CancellationToken);
        (await store.TryClaimAsync("released", anHourFromNow, TestContext.Current.CancellationToken))
            .ShouldBeTrue();

        await store.TryClaimAsync("expired", DateTimeOffset.UtcNow.Subtract(1.Hours()),
            TestContext.Current.CancellationToken);
        (await store.DeleteExpiredAsync(DateTimeOffset.UtcNow, TestContext.Current.CancellationToken))
            .ShouldBe(1);
    }

    [Fact]
    public async Task concurrent_claims_of_one_id_produce_exactly_one_winner_under_string_comparison()
    {
        var store = storeFor(await startHostAsync(MessageDeduplicationMode.CompareByString));

        var expires = anHourFromNow;

        var results = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => store.TryClaimAsync("contended", expires, TestContext.Current.CancellationToken)));

        results.Count(x => x).ShouldBe(1);
    }

    // ---------------------------------------------------------------------------------------------
    // Switching modes, in both directions, without losing a claim
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// GH-4757's upgrade guarantee, and the reason the two modes do not share a table.
    ///
    /// <para>
    /// An application that had deduplication on now maps to
    /// <see cref="MessageDeduplicationMode.CompareByHash" />, so the next deploy runs against a database
    /// that already has <c>wolverine_deduplication</c> with live claims in it. Weasel creates the new
    /// table and <b>does not touch the old one</b>: no startup failure, no drop, and every claim still
    /// where it was, expiring on its own <c>expires</c>. Asserted by count AND by value, because a
    /// surviving-but-emptied table would pass a count-only check.
    /// </para>
    /// </summary>
    [Fact]
    public async Task switching_to_hash_comparison_leaves_the_original_tables_rows_untouched()
    {
        var before = await startHostAsync(MessageDeduplicationMode.CompareByString);
        var store = storeFor(before);

        await store.TryClaimAsync("pre-switch-1", anHourFromNow, TestContext.Current.CancellationToken);
        await store.TryClaimAsync("pre-switch-2", anHourFromNow, TestContext.Current.CancellationToken);

        (await storedIdsAsync(before, deduplicationTableName)).Length.ShouldBe(2);
        await before.StopAsync(TestContext.Current.CancellationToken);

        // Second host, hash mode, SAME database, and deliberately no rebuild -- this IS the upgrade.
        var after = await startHostAsync(MessageDeduplicationMode.CompareByHash, rebuild: false);
        await after.GetRuntime().Storage.Admin.MigrateAsync();

        (await storedIdsAsync(after, deduplicationTableName))
            .ShouldBe(["pre-switch-1", "pre-switch-2"], ignoreOrder: true);

        // And the new table works.
        var hashed = storeFor(after);
        (await hashed.TryClaimAsync("post-switch", anHourFromNow, TestContext.Current.CancellationToken))
            .ShouldBeTrue();
        (await hashed.TryClaimAsync("post-switch", anHourFromNow, TestContext.Current.CancellationToken))
            .ShouldBeFalse();
    }

    /// <summary>
    /// The same in reverse, which is what makes <see cref="MessageDeduplicationMode.CompareByString" /> a
    /// real rollback rather than a one-way door: the hashed table's claims are still there if the
    /// application switches forward again.
    /// </summary>
    [Fact]
    public async Task switching_back_to_string_comparison_leaves_the_hashed_tables_rows_untouched()
    {
        var before = await startHostAsync(MessageDeduplicationMode.CompareByHash);

        await storeFor(before).TryClaimAsync("hashed-claim", anHourFromNow,
            TestContext.Current.CancellationToken);
        (await storedIdsAsync(before, hashedDeduplicationTableName)).Length.ShouldBe(1);
        await before.StopAsync(TestContext.Current.CancellationToken);

        var after = await startHostAsync(MessageDeduplicationMode.CompareByString, rebuild: false);
        await after.GetRuntime().Storage.Admin.MigrateAsync();

        (await storedIdsAsync(after, hashedDeduplicationTableName)).ShouldBe(["hashed-claim"]);

        var strings = storeFor(after);
        (await strings.TryClaimAsync("string-claim", anHourFromNow, TestContext.Current.CancellationToken))
            .ShouldBeTrue();
        (await strings.TryClaimAsync("string-claim", anHourFromNow, TestContext.Current.CancellationToken))
            .ShouldBeFalse();
    }

    // ---------------------------------------------------------------------------------------------
    // None
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// <see cref="MessageDeduplicationMode.None" /> builds no real store, so codegen refuses a
    /// <c>[Deduplicated]</c> chain at bootstrap rather than passing every message. (That the table is
    /// not PROVISIONED is pinned by <c>RecurringMessageCompliance</c>'s schema-neutrality fact, which
    /// owns a schema no other mode has touched.)
    /// </summary>
    [Fact]
    public async Task none_leaves_the_null_store_in_place()
    {
        var store = storeFor(await startHostAsync(MessageDeduplicationMode.None));

        store.Enabled.ShouldBeFalse();

        // NullDeduplicationStore answers "yes, that's new" to everything -- see its own docs for why
        // that, rather than "no", is the safe direction for a misconfigured host.
        (await store.TryClaimAsync("anything", anHourFromNow, TestContext.Current.CancellationToken))
            .ShouldBeTrue();
    }

    // ---------------------------------------------------------------------------------------------
    // GH-5108 -- claiming inside the caller's own transaction
    // ---------------------------------------------------------------------------------------------

    private static async Task<bool> claimInTransactionAsync(IHost host, string id, bool commit)
    {
        var database = (IMessageDatabase)host.GetRuntime().Storage;
        var store = database.Deduplication.ShouldBeAssignableTo<ITransactionalDeduplicationStore>()!;

        await using var conn = await database.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var tx = await conn.BeginTransactionAsync(TestContext.Current.CancellationToken);

        var claimed = await store.TryClaimAsync(conn, tx, id, anHourFromNow, TestContext.Current.CancellationToken);

        // Committing after a refusal as well: a duplicate must leave the caller's transaction usable
        if (commit)
        {
            await tx.CommitAsync(TestContext.Current.CancellationToken);
        }
        else
        {
            await tx.RollbackAsync(TestContext.Current.CancellationToken);
        }

        return claimed;
    }

    [Theory]
    [InlineData(MessageDeduplicationMode.CompareByHash)]
    [InlineData(MessageDeduplicationMode.CompareByString)]
    public async Task a_claim_in_a_rolled_back_transaction_was_never_made(MessageDeduplicationMode mode)
    {
        var host = await startHostAsync(mode);

        (await claimInTransactionAsync(host, "rolled-back", commit: false)).ShouldBeTrue();

        (await storeFor(host).TryClaimAsync("rolled-back", anHourFromNow, TestContext.Current.CancellationToken))
            .ShouldBeTrue("the rollback should have taken the claim with it");
    }

    [Theory]
    [InlineData(MessageDeduplicationMode.CompareByHash)]
    [InlineData(MessageDeduplicationMode.CompareByString)]
    public async Task a_claim_in_a_committed_transaction_refuses_the_next_one(MessageDeduplicationMode mode)
    {
        var host = await startHostAsync(mode);

        (await claimInTransactionAsync(host, "committed", commit: true)).ShouldBeTrue();

        (await storeFor(host).TryClaimAsync("committed", anHourFromNow, TestContext.Current.CancellationToken))
            .ShouldBeFalse();
    }

    [Theory]
    [InlineData(MessageDeduplicationMode.CompareByHash)]
    [InlineData(MessageDeduplicationMode.CompareByString)]
    public async Task an_already_claimed_id_is_refused_in_a_transaction_that_can_still_commit(
        MessageDeduplicationMode mode)
    {
        var host = await startHostAsync(mode);

        (await storeFor(host).TryClaimAsync("taken", anHourFromNow, TestContext.Current.CancellationToken))
            .ShouldBeTrue();

        (await claimInTransactionAsync(host, "taken", commit: true)).ShouldBeFalse();
    }

    // ---------------------------------------------------------------------------------------------
    // D1 -- the claim records the envelope that took it
    // ---------------------------------------------------------------------------------------------

    private static IEnvelopeAwareDeduplicationStore envelopeAwareStoreFor(IHost host)
        => storeFor(host).ShouldBeAssignableTo<IEnvelopeAwareDeduplicationStore>()!;

    /// <summary>
    /// D1 (durable compliance findings, 2026-10-22). This store's claim is committed ahead of the handler and given
    /// back only by the compensating release, which a node that dies mid-handler never reaches. Inbox recovery then
    /// brings the SAME envelope back, and it used to be refused as a duplicate of its own stranded claim: the work
    /// was lost for the whole deduplication window. The same envelope now takes its claim over.
    /// </summary>
    [Theory]
    [InlineData(MessageDeduplicationMode.CompareByHash)]
    [InlineData(MessageDeduplicationMode.CompareByString)]
    public async Task the_same_envelope_takes_over_a_claim_it_left_behind(MessageDeduplicationMode mode)
    {
        var store = envelopeAwareStoreFor(await startHostAsync(mode));
        var envelopeId = Guid.NewGuid();

        (await store.TryClaimAsync("stranded", envelopeId, anHourFromNow, TestContext.Current.CancellationToken))
            .ShouldBeTrue();

        (await store.TryClaimAsync("stranded", envelopeId, anHourFromNow, TestContext.Current.CancellationToken))
            .ShouldBeTrue("the copy inbox recovery brings back is the envelope that took the claim");
    }

    /// <summary>
    /// D1, the half that keeps the feature: taking a claim over is only for the envelope that took it. A different
    /// envelope with the same logical id is a genuine duplicate and is still refused.
    /// </summary>
    [Theory]
    [InlineData(MessageDeduplicationMode.CompareByHash)]
    [InlineData(MessageDeduplicationMode.CompareByString)]
    public async Task a_different_envelope_is_still_refused(MessageDeduplicationMode mode)
    {
        var host = await startHostAsync(mode);
        var store = envelopeAwareStoreFor(host);

        (await store.TryClaimAsync("held", Guid.NewGuid(), anHourFromNow, TestContext.Current.CancellationToken))
            .ShouldBeTrue();

        (await store.TryClaimAsync("held", Guid.NewGuid(), anHourFromNow, TestContext.Current.CancellationToken))
            .ShouldBeFalse();

        // And the envelope-less claim is refused too, as it always was
        (await storeFor(host).TryClaimAsync("held", anHourFromNow, TestContext.Current.CancellationToken))
            .ShouldBeFalse();
    }

    /// <summary>
    /// D1. A claim with no envelope recorded -- written through the envelope-less claim, or before the column
    /// existed -- keeps the original behaviour: nobody can take it over.
    /// </summary>
    [Theory]
    [InlineData(MessageDeduplicationMode.CompareByHash)]
    [InlineData(MessageDeduplicationMode.CompareByString)]
    public async Task a_claim_recorded_without_an_envelope_is_never_taken_over(MessageDeduplicationMode mode)
    {
        var host = await startHostAsync(mode);

        (await storeFor(host).TryClaimAsync("anonymous", anHourFromNow, TestContext.Current.CancellationToken))
            .ShouldBeTrue();

        (await envelopeAwareStoreFor(host).TryClaimAsync("anonymous", Guid.NewGuid(), anHourFromNow,
            TestContext.Current.CancellationToken)).ShouldBeFalse();
    }

    /// <summary>
    /// D1. Taking a claim over renews its expiry, so the recovered attempt holds the claim for a full window rather
    /// than whatever was left of the dead attempt's, and the reaper does not take it out from under the handler.
    /// </summary>
    [Theory]
    [InlineData(MessageDeduplicationMode.CompareByHash)]
    [InlineData(MessageDeduplicationMode.CompareByString)]
    public async Task taking_a_claim_over_renews_its_expiry(MessageDeduplicationMode mode)
    {
        var host = await startHostAsync(mode);
        var store = envelopeAwareStoreFor(host);
        var envelopeId = Guid.NewGuid();

        await store.TryClaimAsync("renewed", envelopeId, DateTimeOffset.UtcNow.Subtract(1.Hours()),
            TestContext.Current.CancellationToken);
        (await store.TryClaimAsync("renewed", envelopeId, anHourFromNow, TestContext.Current.CancellationToken))
            .ShouldBeTrue();

        (await storeFor(host).DeleteExpiredAsync(DateTimeOffset.UtcNow, TestContext.Current.CancellationToken))
            .ShouldBe(0);
        (await store.TryClaimAsync("renewed", Guid.NewGuid(), anHourFromNow, TestContext.Current.CancellationToken))
            .ShouldBeFalse();
    }

    /// <summary>
    /// D1. The release still gives the claim back whoever took it, so a failed attempt does not poison the id for
    /// a later, different envelope.
    /// </summary>
    [Theory]
    [InlineData(MessageDeduplicationMode.CompareByHash)]
    [InlineData(MessageDeduplicationMode.CompareByString)]
    public async Task releasing_an_envelope_claim_lets_another_envelope_claim_the_id(MessageDeduplicationMode mode)
    {
        var host = await startHostAsync(mode);
        var store = envelopeAwareStoreFor(host);

        await store.TryClaimAsync("released", Guid.NewGuid(), anHourFromNow, TestContext.Current.CancellationToken);
        await storeFor(host).ReleaseAsync("released", TestContext.Current.CancellationToken);

        (await store.TryClaimAsync("released", Guid.NewGuid(), anHourFromNow, TestContext.Current.CancellationToken))
            .ShouldBeTrue();
    }

    /// <summary>
    /// D1. The race the feature exists for, through the envelope-aware claim: twenty different envelopes, one id,
    /// exactly one winner. Taking over must not open a door for a different envelope.
    /// </summary>
    [Theory]
    [InlineData(MessageDeduplicationMode.CompareByHash)]
    [InlineData(MessageDeduplicationMode.CompareByString)]
    public async Task concurrent_envelope_claims_of_one_id_produce_exactly_one_winner(MessageDeduplicationMode mode)
    {
        var store = envelopeAwareStoreFor(await startHostAsync(mode));
        var expires = anHourFromNow;

        var results = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => store.TryClaimAsync("contended", Guid.NewGuid(), expires,
                TestContext.Current.CancellationToken)));

        results.Count(x => x).ShouldBe(1);
    }

    /// <summary>
    /// D1's upgrade guarantee. The envelope column is additive and nullable, so a deployment that already has the
    /// deduplication table, with live claims in it and no envelope column, migrates without losing a claim, and
    /// those claims keep refusing every envelope.
    /// </summary>
    [Theory]
    [InlineData(MessageDeduplicationMode.CompareByHash)]
    [InlineData(MessageDeduplicationMode.CompareByString)]
    public async Task an_existing_table_without_the_envelope_column_migrates_and_keeps_its_claims(
        MessageDeduplicationMode mode)
    {
        var before = await startHostAsync(mode);
        var table = mode == MessageDeduplicationMode.CompareByHash
            ? hashedDeduplicationTableName
            : deduplicationTableName;

        await storeFor(before).TryClaimAsync("pre-upgrade", anHourFromNow, TestContext.Current.CancellationToken);

        var database = (IMessageDatabase)before.GetRuntime().Storage;
        await using (var drop = database.DataSource.CreateCommand(
                         $"alter table {table} drop column {DatabaseConstants.DeduplicationEnvelopeId}"))
        {
            await drop.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        await before.StopAsync(TestContext.Current.CancellationToken);

        var after = await startHostAsync(mode, rebuild: false);
        await after.GetRuntime().Storage.Admin.MigrateAsync();

        (await columnsAsync(after, table)).ShouldContain(DatabaseConstants.DeduplicationEnvelopeId);
        (await storedIdsAsync(after, table)).ShouldBe(["pre-upgrade"]);

        (await envelopeAwareStoreFor(after).TryClaimAsync("pre-upgrade", Guid.NewGuid(), anHourFromNow,
            TestContext.Current.CancellationToken)).ShouldBeFalse();
    }

    private string tableFor(MessageDeduplicationMode mode) => mode == MessageDeduplicationMode.CompareByHash
        ? hashedDeduplicationTableName
        : deduplicationTableName;

    /// <summary>The table the way it was before D1: no envelope column.</summary>
    private static async Task dropEnvelopeColumnAsync(IHost host, string table)
    {
        var database = (IMessageDatabase)host.GetRuntime().Storage;
        await using var drop = database.DataSource.CreateCommand(
            $"alter table {table} drop column {DatabaseConstants.DeduplicationEnvelopeId}");
        await drop.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// D1 follow-up. An application on <c>AutoCreate.None</c> that upgrades without migrating still has the table
    /// without <c>envelope_id</c>. Its claims have to keep working exactly as they did before -- no exception, and
    /// the original semantics, including a repeat from the same envelope being refused -- rather than failing every
    /// <c>[Deduplicated]</c> handler on an INSERT naming a column that is not there.
    /// </summary>
    [Theory]
    [InlineData(MessageDeduplicationMode.CompareByHash)]
    [InlineData(MessageDeduplicationMode.CompareByString)]
    public async Task an_unmigrated_table_keeps_the_original_claim_semantics(MessageDeduplicationMode mode)
    {
        var host = await startHostAsync(mode);
        await dropEnvelopeColumnAsync(host, tableFor(mode));

        var store = envelopeAwareStoreFor(host);
        var envelopeId = Guid.NewGuid();

        (await store.TryClaimAsync("unmigrated", envelopeId, anHourFromNow, TestContext.Current.CancellationToken))
            .ShouldBeTrue();

        // The original semantics: nothing records the envelope, so nothing can take the claim over
        (await store.TryClaimAsync("unmigrated", envelopeId, anHourFromNow, TestContext.Current.CancellationToken))
            .ShouldBeFalse();
        (await store.TryClaimAsync("unmigrated", Guid.NewGuid(), anHourFromNow, TestContext.Current.CancellationToken))
            .ShouldBeFalse();
        (await storeFor(host).TryClaimAsync("unmigrated", anHourFromNow, TestContext.Current.CancellationToken))
            .ShouldBeFalse();

        await storeFor(host).ReleaseAsync("unmigrated", TestContext.Current.CancellationToken);
        (await store.TryClaimAsync("unmigrated", Guid.NewGuid(), anHourFromNow, TestContext.Current.CancellationToken))
            .ShouldBeTrue();
    }

    /// <summary>
    /// D1 follow-up. The column is detected once per process, so the store picks it up after the migration has been
    /// applied and the application restarted -- here, a second host against the same, now migrated, database.
    /// </summary>
    [Theory]
    [InlineData(MessageDeduplicationMode.CompareByHash)]
    [InlineData(MessageDeduplicationMode.CompareByString)]
    public async Task after_the_migration_and_a_restart_claims_record_the_envelope(MessageDeduplicationMode mode)
    {
        var before = await startHostAsync(mode);
        await dropEnvelopeColumnAsync(before, tableFor(mode));

        // Detected as absent, and the claim still works
        (await envelopeAwareStoreFor(before).TryClaimAsync("pre-migration", Guid.NewGuid(), anHourFromNow,
            TestContext.Current.CancellationToken)).ShouldBeTrue();
        await before.StopAsync(TestContext.Current.CancellationToken);

        var after = await startHostAsync(mode, rebuild: false);
        await after.GetRuntime().Storage.Admin.MigrateAsync();

        var store = envelopeAwareStoreFor(after);
        var envelopeId = Guid.NewGuid();

        (await store.TryClaimAsync("post-migration", envelopeId, anHourFromNow, TestContext.Current.CancellationToken))
            .ShouldBeTrue();
        (await store.TryClaimAsync("post-migration", envelopeId, anHourFromNow, TestContext.Current.CancellationToken))
            .ShouldBeTrue("the migrated table records the envelope, so the same envelope takes its claim over");

        // The claim made before the migration has no envelope recorded and is never taken over
        (await store.TryClaimAsync("pre-migration", Guid.NewGuid(), anHourFromNow, TestContext.Current.CancellationToken))
            .ShouldBeFalse();
    }
}
