using IntegrationTests;
using Microsoft.Data.SqlClient;
using Shouldly;
using Weasel.Core;
using Weasel.SqlServer;
using Wolverine;
using Wolverine.RDBMS;
using Wolverine.SqlServer.Schema;

namespace SqlServerTests;

// GH-3279: DeadLettersTable provisions a filtered index on `replayable` (and, when DLQ expiration is
// enabled, on `expires`) so the durability agent's replay/cleanup queries stop scanning the whole
// table. These tests prove the indexes are created AND that their filter predicates round-trip
// through sys.indexes so a subsequent migration reports no drift (SqlServer stores filter
// definitions like `([replayable]=(1))`, which must canonicalize back to the configured predicate).
[Collection("sqlserver")]
public class DeadLetterTable_index_creation : IAsyncLifetime
{
    private SqlConnection theConnection = null!;

    public async Task InitializeAsync()
    {
        theConnection = new SqlConnection(Servers.SqlServerConnectionString);
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
