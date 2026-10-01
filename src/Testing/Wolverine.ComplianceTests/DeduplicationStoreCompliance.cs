using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;
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

    [Fact]
    public async Task string_comparison_provisions_only_the_original_two_column_shape()
    {
        var host = await startHostAsync(MessageDeduplicationMode.CompareByString);

        (await columnsAsync(host, deduplicationTableName))
            .ShouldBe([DatabaseConstants.DeduplicationId, DatabaseConstants.Expires], ignoreOrder: true);

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
}
