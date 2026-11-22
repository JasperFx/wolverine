using Wolverine;
using Wolverine.ComplianceTests.Sagas;
using Wolverine.RDBMS;
using Wolverine.Sqlite;

namespace SqliteTests.Sagas;

// GH-5839: completing a saga from outside its handlers, in the lightweight SQLite saga tables
public class saga_termination_5839 : SagaTerminationCompliance
{
    private SqliteTestDatabase? _database;

    protected override void ConfigureStorage(WolverineOptions opts)
    {
        _database = Servers.CreateDatabase(nameof(saga_termination_5839));
        opts.AddSagaType<TerminableSaga>();
        opts.AddSagaType<RetainedTerminableSaga>();
        opts.PersistMessagesWithSqlite(_database.ConnectionString);
    }

    protected override ValueTask CleanUpAsync()
    {
        _database?.Dispose();
        return ValueTask.CompletedTask;
    }
}
