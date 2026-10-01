using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Sqlite;

namespace SqliteTests;

[Collection("sqlite")]
public class deduplication_store_compliance : DeduplicationStoreCompliance, IAsyncLifetime
{
    private readonly SqliteTestDatabase _database = Servers.CreateDatabase(nameof(deduplication_store_compliance));

    protected override void configurePersistence(WolverineOptions opts)
    {
        opts.PersistMessagesWithSqlite(_database.ConnectionString);
    }

    // No schema name configured, so TablePrefixing leaves the bare table name (GH-3943).
    protected override string deduplicationTableName => "wolverine_deduplication";

    // A SQLite TEXT column's default collation is BINARY, so the string mode never had the defect here.
    protected override bool stringComparisonIsCaseSensitive => true;

    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        await base.DisposeAsync();
        _database.Dispose();
    }
}
