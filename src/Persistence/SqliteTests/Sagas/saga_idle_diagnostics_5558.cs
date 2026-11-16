using Wolverine.RDBMS;
using Wolverine;
using Wolverine.ComplianceTests.Sagas;
using Wolverine.Sqlite;

namespace SqliteTests.Sagas;

// GH-5558: idle saga diagnostics over the lightweight SQLite saga tables, where last_modified is sortable UTC text
public class saga_idle_diagnostics_5558 : SagaIdleDiagnosticsCompliance
{
    private SqliteTestDatabase? _database;

    protected override void ConfigureStorage(WolverineOptions opts)
    {
        _database = Servers.CreateDatabase(nameof(saga_idle_diagnostics_5558));
        opts.AddSagaType<IdleDiagnosticsSaga>();
        opts.PersistMessagesWithSqlite(_database.ConnectionString);
    }

    protected override ValueTask CleanUpAsync()
    {
        _database?.Dispose();
        return ValueTask.CompletedTask;
    }
}
