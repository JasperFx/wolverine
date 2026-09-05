using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Sqlite;

namespace SqliteTests;

[Collection("sqlite")]
public class recurring_message_compliance : RecurringMessageCompliance, IAsyncLifetime
{
    // One file for the whole class so the restart facts (pause survives, adoption) see the same
    // database from their second host.
    private readonly SqliteTestDatabase _database = Servers.CreateDatabase(nameof(recurring_message_compliance));

    protected override void configurePersistence(WolverineOptions opts)
    {
        opts.PersistMessagesWithSqlite(_database.ConnectionString);
    }

    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        await base.DisposeAsync();
        _database.Dispose();
    }
}
