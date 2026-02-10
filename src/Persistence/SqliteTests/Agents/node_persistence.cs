using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Weasel.Sqlite;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;
using Wolverine.Sqlite;

namespace SqliteTests.Agents;

public class node_persistence : NodePersistenceCompliance
{
    private readonly string _connectionString = Servers.CreateInMemoryConnectionString();

    protected override async Task<IMessageStore> buildCleanMessageStore()
    {
        var settings = new DatabaseSettings
        {
            ConnectionString = _connectionString,
            SchemaName = "main",
            Role = MessageStoreRole.Main
        };

        var dataSource = new SqliteDataSource(_connectionString);
        var database = new SqliteMessageStore(settings, new DurabilitySettings(),
            dataSource,
            NullLogger<SqliteMessageStore>.Instance);

        await database.Admin.MigrateAsync();

        return database;
    }
}
