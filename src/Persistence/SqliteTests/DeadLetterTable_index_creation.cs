using Microsoft.Data.Sqlite;
using Shouldly;
using Weasel.Core;
using Weasel.Sqlite;
using Wolverine;
using Wolverine.RDBMS;
using Wolverine.Sqlite.Schema;
using Xunit;

namespace SqliteTests;

// GH-3279: DeadLettersTable provisions partial indexes on `replayable` (and `expires` when DLQ
// expiration is enabled) so the durability agent's replay/cleanup queries stop scanning the whole
// table. These tests prove the indexes are created AND that their predicates round-trip through
// sqlite_master so a subsequent migration reports no drift.
public class DeadLetterTable_index_creation : IAsyncLifetime
{
    private SqliteTestDatabase _database = null!;
    private SqliteConnection theConnection = null!;

    public async Task InitializeAsync()
    {
        _database = Servers.CreateDatabase(nameof(DeadLetterTable_index_creation));
        theConnection = new SqliteConnection(_database.ConnectionString);
        await theConnection.OpenAsync();
    }

    public async Task DisposeAsync()
    {
        await theConnection.DisposeAsync();
        _database.Dispose();
    }

    [Fact]
    public async Task creates_the_replayable_index_and_is_stable_without_expiration()
    {
        var durability = new DurabilitySettings { DeadLetterQueueExpirationEnabled = false };
        var table = new DeadLettersTable(durability, "main");

        table.Indexes.ShouldContain(x => x.Name.Contains("replayable"));
        table.Indexes.ShouldNotContain(x => x.Name.Contains("expires"));

        await table.ApplyChangesAsync(theConnection);

        var delta = await table.FindDeltaAsync(theConnection);
        delta.Difference.ShouldBe(SchemaPatchDifference.None);
    }

    [Fact]
    public async Task creates_replayable_and_expires_indexes_and_is_stable_with_expiration()
    {
        var durability = new DurabilitySettings { DeadLetterQueueExpirationEnabled = true };
        var table = new DeadLettersTable(durability, "main");

        table.Indexes.ShouldContain(x => x.Name.Contains("replayable"));
        table.Indexes.ShouldContain(x => x.Name.Contains("expires"));

        await table.ApplyChangesAsync(theConnection);

        var delta = await table.FindDeltaAsync(theConnection);
        delta.Difference.ShouldBe(SchemaPatchDifference.None);
    }
}
