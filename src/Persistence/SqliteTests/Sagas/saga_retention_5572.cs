using Wolverine;
using Wolverine.ComplianceTests.Sagas;
using Wolverine.RDBMS;
using Wolverine.Sqlite;

namespace SqliteTests.Sagas;

// GH-5572: retained, completed sagas in the lightweight SQLite saga tables, where completed_at is sortable UTC text
public class saga_retention_5572 : SagaRetentionCompliance
{
    private SqliteTestDatabase? _database;

    protected override void ConfigureStorage(WolverineOptions opts)
    {
        _database = Servers.CreateDatabase(nameof(saga_retention_5572));
        opts.AddSagaType<RetainedSaga>();
        opts.PersistMessagesWithSqlite(_database.ConnectionString);
    }

    protected override ValueTask CleanUpAsync()
    {
        _database?.Dispose();
        return ValueTask.CompletedTask;
    }
}
