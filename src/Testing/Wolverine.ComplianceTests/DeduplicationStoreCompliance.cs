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
/// </summary>
public abstract class DeduplicationStoreCompliance : IAsyncLifetime
{
    private readonly List<IHost> _hosts = [];

    /// <summary>Wire this provider's message persistence into the options.</summary>
    protected abstract void configurePersistence(WolverineOptions opts);

    /// <summary>
    /// The rendered name of the deduplication table for the schema <see cref="configurePersistence" />
    /// uses — schema-qualified on engines with schemas, prefixed on SQLite (GH-3943).
    /// </summary>
    protected abstract string deduplicationTableName { get; }

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

        // Drop and recreate rather than migrate: each fact wants a known-empty table in a known
        // shape, and the two modes provision DIFFERENT shapes into the same name. The one fact that
        // cares about migrating between the shapes passes rebuild: false and drives the migration
        // itself.
        if (rebuild) await host.RebuildAllEnvelopeStorageAsync();

        return host;
    }

    private static IDeduplicationStore storeFor(IHost host) => host.GetRuntime().Storage.Deduplication;

    private static DateTimeOffset anHourFromNow => DateTimeOffset.UtcNow.Add(1.Hours());

    /// <summary>
    /// The column names on the provisioned table, read off a zero-row reader so this works the same on
    /// all four engines without a per-provider schema API.
    /// </summary>
    private async Task<string[]> columnsAsync(IHost host)
    {
        var database = (IMessageDatabase)host.GetRuntime().Storage;

        await using var cmd = database.DataSource.CreateCommand(
            $"select * from {deduplicationTableName} where 1 = 0");
        await using var reader = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);

        return Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
    }

    private async Task<string?> readStoredIdAsync(IHost host, string deduplicationId)
    {
        var database = (IMessageDatabase)host.GetRuntime().Storage;

        await using var cmd = database.DataSource.CreateCommand(
            $"select {DatabaseConstants.DeduplicationId} from {deduplicationTableName}");
        await using var reader = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);

        var found = new List<string>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            found.Add(await reader.GetFieldValueAsync<string>(0, TestContext.Current.CancellationToken));
        }

        return found.FirstOrDefault(x => x == deduplicationId);
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

        (await readStoredIdAsync(host, "Invoice-17|José")).ShouldBe("Invoice-17|José");
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

    [Fact]
    public async Task hash_comparison_provisions_the_hash_column_and_keeps_the_readable_one()
    {
        var columns = await columnsAsync(await startHostAsync(MessageDeduplicationMode.CompareByHash));

        columns.ShouldContain(DatabaseConstants.DeduplicationHash);
        columns.ShouldContain(DatabaseConstants.DeduplicationId);
        columns.ShouldContain(DatabaseConstants.Expires);
    }

    // ---------------------------------------------------------------------------------------------
    // CompareByString -- the escape hatch, which has to preserve the old behaviour rather than
    // quietly get the new one
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task string_comparison_provisions_the_original_two_column_shape()
    {
        var columns = await columnsAsync(await startHostAsync(MessageDeduplicationMode.CompareByString));

        columns.ShouldBe([DatabaseConstants.DeduplicationId, DatabaseConstants.Expires],
            ignoreOrder: true);
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
    // Upgrading between the shapes
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// GH-4757's upgrade story, asserted rather than asserted-about. An application that had
    /// deduplication on is now in <see cref="MessageDeduplicationMode.CompareByHash" />, so the next
    /// deploy migrates the table it already has.
    ///
    /// <para>
    /// The thing being proved is narrow and specific: that the migration RUNS and leaves a table that
    /// claims correctly. What it deliberately does NOT claim is that in-flight claims survive — they
    /// cannot. The hash is computed in the application, so there is no SQL expression that could
    /// backfill it for existing rows, and the arbitrating key is therefore empty on the other side.
    /// That is bounded by <c>DeduplicationWindow</c> and documented; it is not papered over here.
    /// </para>
    /// </summary>
    [Fact]
    public async Task upgrading_from_the_string_shape_to_the_hash_shape_leaves_a_working_table()
    {
        var before = await startHostAsync(MessageDeduplicationMode.CompareByString);
        await storeFor(before).TryClaimAsync("pre-upgrade", anHourFromNow,
            TestContext.Current.CancellationToken);
        await before.StopAsync(TestContext.Current.CancellationToken);

        var after = await startHostAsync(MessageDeduplicationMode.CompareByHash, rebuild: false);
        await after.GetRuntime().Storage.Admin.MigrateAsync();

        (await columnsAsync(after)).ShouldContain(DatabaseConstants.DeduplicationHash);

        var store = storeFor(after);

        (await store.TryClaimAsync("post-upgrade", anHourFromNow, TestContext.Current.CancellationToken))
            .ShouldBeTrue();
        (await store.TryClaimAsync("post-upgrade", anHourFromNow, TestContext.Current.CancellationToken))
            .ShouldBeFalse();

        // And the cost, pinned rather than merely written down: the claim taken before the upgrade is
        // gone, so the id it was protecting is claimable again.
        (await store.TryClaimAsync("pre-upgrade", anHourFromNow, TestContext.Current.CancellationToken))
            .ShouldBeTrue("in-flight claims cannot survive the upgrade -- the hash cannot be backfilled");
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
