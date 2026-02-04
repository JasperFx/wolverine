using IntegrationTests;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.MySql;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;
using Wolverine.RDBMS.Sagas;

namespace MySqlTests.Agents;

[Collection("mysql")]
public class node_persistence : NodePersistenceCompliance
{
    protected override async Task<IMessageStore> buildCleanMessageStore()
    {
        await using var conn = new MySqlConnection(Servers.MySqlConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DROP DATABASE IF EXISTS `nodes`";
        await cmd.ExecuteNonQueryAsync();
        await conn.CloseAsync();

        var dataSource = MySqlDataSourceFactory.Create(Servers.MySqlConnectionString);
        var settings = new DatabaseSettings
        {
            ConnectionString = Servers.MySqlConnectionString,
            SchemaName = "nodes",
            Role = MessageStoreRole.Main
        };

        var database = new MySqlMessageStore(settings, new DurabilitySettings(), dataSource,
            NullLogger<MySqlMessageStore>.Instance, Array.Empty<SagaTableDefinition>());

        await database.Admin.MigrateAsync();

        return database;
    }
}
