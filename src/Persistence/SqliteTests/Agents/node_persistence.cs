using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Weasel.Sqlite;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;
using Wolverine.Sqlite;

namespace SqliteTests.Agents;

public class node_persistence : NodePersistenceCompliance, IAsyncLifetime
{
    private readonly SqliteTestDatabase _database = Servers.CreateDatabase(nameof(node_persistence));

    protected override async Task<IMessageStore> buildCleanMessageStore()
    {
        var settings = new DatabaseSettings
        {
            ConnectionString = _database.ConnectionString,
            SchemaName = "main",
            Role = MessageStoreRole.Main
        };

        var dataSource = new SqliteDataSource(_database.ConnectionString);
        var database = new SqliteMessageStore(settings, new DurabilitySettings(),
            dataSource,
            NullLogger<SqliteMessageStore>.Instance);

        await database.Admin.MigrateAsync();

        return database;
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        _database.Dispose();
    }
}
