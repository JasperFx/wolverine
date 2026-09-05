using IntegrationTests;
using Npgsql;
using Shouldly;
using Weasel.Core;
using Weasel.Postgresql;
using Wolverine;
using Wolverine.Postgresql.Schema;
using Wolverine.RDBMS;

namespace PostgresqlTests;

// GH-4316: the 5-second recovery poll and the per-listener recovery load ask for
// `owner_id = 0` — exactly the value the GH-3971 owner index excludes — and the expired-handled
// cleanup filters on `status = 'Handled' and keep_until <= now` with no index at all, so all of
// them were full scans of an inbox dominated by retained Handled rows. The envelope tables now
// provision partial indexes for the recoverable and expired-handled slices. These tests prove the
// indexes are created AND that their compound predicates round-trip through pg_get_indexdef so a
// subsequent migration reports no drift (a mismatched predicate would make Weasel drop+recreate
// the index on every startup).
public class EnvelopeTables_recovery_index_creation : IAsyncLifetime
{
    private NpgsqlConnection theConnection = null!;

    public async ValueTask InitializeAsync()
    {
        theConnection = new NpgsqlConnection(Servers.PostgresConnectionString);
        await theConnection.OpenAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await theConnection.DisposeAsync();
    }

    [Fact]
    public async Task incoming_recovery_indexes_are_created_and_stable()
    {
        await theConnection.ResetSchemaAsync("env_idx_incoming", ct: TestContext.Current.CancellationToken);

        var table = new IncomingEnvelopeTable(new DurabilitySettings(), "env_idx_incoming");

        table.Indexes.ShouldContain(x => x.Name.Contains("recover"));
        table.Indexes.ShouldContain(x => x.Name.Contains("keep_until"));

        await table.ApplyChangesAsync(theConnection, ct: TestContext.Current.CancellationToken);

        // Re-reading the just-created schema must report NO difference. If a partial-index
        // predicate did not round-trip, this would come back as Update and thrash on every startup.
        var delta = await table.FindDeltaAsync(theConnection, TestContext.Current.CancellationToken);
        delta.Difference.ShouldBe(SchemaPatchDifference.None);
    }

    [Fact]
    public async Task incoming_recovery_indexes_are_stable_with_inbox_partitioning()
    {
        await theConnection.ResetSchemaAsync("env_idx_part", ct: TestContext.Current.CancellationToken);

        var durability = new DurabilitySettings { EnableInboxPartitioning = true };
        var table = new IncomingEnvelopeTable(durability, "env_idx_part");

        await table.ApplyChangesAsync(theConnection, ct: TestContext.Current.CancellationToken);

        var delta = await table.FindDeltaAsync(theConnection, TestContext.Current.CancellationToken);
        delta.Difference.ShouldBe(SchemaPatchDifference.None);
    }

    [Fact]
    public async Task outgoing_recovery_index_is_created_and_stable()
    {
        await theConnection.ResetSchemaAsync("env_idx_outgoing", ct: TestContext.Current.CancellationToken);

        var table = new OutgoingEnvelopeTable(new DurabilitySettings(), "env_idx_outgoing");

        table.Indexes.ShouldContain(x => x.Name.Contains("recover"));

        await table.ApplyChangesAsync(theConnection, ct: TestContext.Current.CancellationToken);

        var delta = await table.FindDeltaAsync(theConnection, TestContext.Current.CancellationToken);
        delta.Difference.ShouldBe(SchemaPatchDifference.None);
    }
}
