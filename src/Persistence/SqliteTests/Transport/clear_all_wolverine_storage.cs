using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Sqlite;
using Wolverine.Sqlite.Transport;

namespace SqliteTests.Transport;

public class clear_all_wolverine_storage : ClearAllWolverineStorageCompliance
{
    private readonly SqliteTestDatabase _database =
        Servers.CreateDatabase(nameof(clear_all_wolverine_storage));

    protected override void ConfigureStorage(WolverineOptions options)
    {
        options.UseSqlitePersistenceAndTransport(_database.ConnectionString);

        // Nothing listens to the queue, so nothing drains it out from under the assertions
        // before the reset runs.
        options.PublishAllMessages().ToSqliteQueue(QueueName);
    }

    protected override ValueTask sendToQueueAsync(Envelope envelope)
    {
        return ((SqliteQueue)theQueue).SendAsync(envelope);
    }

    public override async Task DisposeAsync()
    {
        await base.DisposeAsync();
        _database.Dispose();
    }
}
