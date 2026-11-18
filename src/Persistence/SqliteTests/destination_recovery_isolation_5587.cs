using Wolverine;
using Wolverine.Sqlite;
using Wolverine.ComplianceTests.DestinationIsolation;

namespace SqliteTests;

/// <summary>
/// GH-5587 -- see <see cref="DestinationRecoveryIsolationCompliance"/>.
/// </summary>
public class destination_recovery_isolation_5587 : DestinationRecoveryIsolationCompliance, IDisposable
{
    private readonly SqliteTestDatabase _database = Servers.CreateDatabase("destination_isolation");

    protected override void ConfigureStorage(WolverineOptions opts)
    {
        opts.PersistMessagesWithSqlite(_database.ConnectionString);
    }

    public void Dispose()
    {
        _database.Dispose();
    }
}
