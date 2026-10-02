using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Sqlite;

namespace SqliteTests;

[Collection("sqlite")]
public class replayable_deduplication_store_compliance : ReplayableDeduplicationStoreCompliance
{
    private readonly SqliteTestDatabase _database = Servers.CreateDatabase(nameof(replayable_deduplication_store_compliance));

    protected override void configurePersistence(WolverineOptions opts)
    {
        opts.PersistMessagesWithSqlite(_database.ConnectionString);
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        _database.Dispose();
    }
}
