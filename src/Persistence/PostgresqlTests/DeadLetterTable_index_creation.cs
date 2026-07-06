using IntegrationTests;
using Npgsql;
using Shouldly;
using Weasel.Core;
using Weasel.Postgresql;
using Wolverine;
using Wolverine.Postgresql.Schema;
using Wolverine.RDBMS;

namespace PostgresqlTests;

// GH-3279: the DLQ replay INSERT-SELECT and cleanup DELETE both filter on `replayable`, and the
// expiration sweep filters on `expires`. Without indexes these seq-scan the whole (potentially
// multi-GB) dead-letter table on every durability cycle. DeadLettersTable now provisions partial
// indexes for both. These tests prove the indexes are created AND — critically — that their
// predicates round-trip through pg_get_indexdef so a subsequent schema migration reports no drift
// (a mismatched predicate would make Weasel drop+recreate the index on every startup).
public class DeadLetterTable_index_creation : IAsyncLifetime
{
    private NpgsqlConnection theConnection = null!;

    public async Task InitializeAsync()
    {
        theConnection = new NpgsqlConnection(Servers.PostgresConnectionString);
        await theConnection.OpenAsync();
    }

    public async Task DisposeAsync()
    {
        await theConnection.DisposeAsync();
    }

    [Fact]
    public async Task creates_the_replayable_index_and_is_stable_without_expiration()
    {
        await theConnection.ResetSchemaAsync("dlq_idx_no_exp");

        var durability = new DurabilitySettings { DeadLetterQueueExpirationEnabled = false };
        var table = new DeadLettersTable(durability, "dlq_idx_no_exp");

        table.Indexes.ShouldContain(x => x.Name.Contains("replayable"));
        table.Indexes.ShouldNotContain(x => x.Name.Contains("expires"));

        await table.ApplyChangesAsync(theConnection);

        // Re-reading the just-created schema must report NO difference. If the partial-index
        // predicate did not round-trip, this would come back as Update and thrash on every startup.
        var delta = await table.FindDeltaAsync(theConnection);
        delta.Difference.ShouldBe(SchemaPatchDifference.None);
    }

    [Fact]
    public async Task creates_replayable_and_expires_indexes_and_is_stable_with_expiration()
    {
        await theConnection.ResetSchemaAsync("dlq_idx_exp");

        var durability = new DurabilitySettings { DeadLetterQueueExpirationEnabled = true };
        var table = new DeadLettersTable(durability, "dlq_idx_exp");

        table.Indexes.ShouldContain(x => x.Name.Contains("replayable"));
        table.Indexes.ShouldContain(x => x.Name.Contains("expires"));

        await table.ApplyChangesAsync(theConnection);

        var delta = await table.FindDeltaAsync(theConnection);
        delta.Difference.ShouldBe(SchemaPatchDifference.None);
    }
}
