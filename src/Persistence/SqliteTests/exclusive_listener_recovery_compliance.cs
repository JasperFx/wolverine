using Wolverine;
using Wolverine.ComplianceTests.ExclusiveListeners;
using Wolverine.Sqlite;

namespace SqliteTests;

/// <summary>
/// GH-3590 -- see <see cref="ExclusiveListenerRecoveryCompliance"/>.
/// </summary>
public class exclusive_listener_recovery_compliance : ExclusiveListenerRecoveryCompliance, IDisposable
{
    private readonly SqliteTestDatabase _database = Servers.CreateDatabase("exclusive_recovery");

    protected override void ConfigureStorage(WolverineOptions options)
    {
        options.PersistMessagesWithSqlite(_database.ConnectionString);
    }

    public void Dispose()
    {
        _database.Dispose();
    }
}
