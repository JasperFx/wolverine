using Wolverine;
using Wolverine.ComplianceTests.Sagas;
using Wolverine.RDBMS;
using Wolverine.Sqlite;

namespace SqliteTests.Sagas;

// GH-5556: a compensating saga's status and log in the lightweight SQLite saga tables
public class saga_compensation_5556 : SagaCompensationCompliance
{
    private SqliteTestDatabase? _database;

    protected override void ConfigureStorage(WolverineOptions opts)
    {
        _database = Servers.CreateDatabase(nameof(saga_compensation_5556));
        opts.AddSagaType<CompensatedSaga>();
        opts.PersistMessagesWithSqlite(_database.ConnectionString);
    }

    protected override ValueTask CleanUpAsync()
    {
        _database?.Dispose();
        return ValueTask.CompletedTask;
    }
}
