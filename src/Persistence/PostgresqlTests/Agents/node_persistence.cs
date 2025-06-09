using IntegrationTests;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Weasel.Postgresql;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Persistence.Durability;
using Wolverine.Postgresql;
using Wolverine.RDBMS;

namespace PostgresqlTests.Agents;

[Collection("marten")]
public class node_persistence : NodePersistenceCompliance
{
    protected override async Task<IMessageStore> buildCleanMessageStore()
    {
        await using (var conn = new NpgsqlConnection(Servers.PostgresConnectionString))
        {
            await conn.OpenAsync();

            await conn.DropSchemaAsync("nodes");

            await conn.CloseAsync();
        }

        var settings = new DatabaseSettings
        {
            ConnectionString = Servers.PostgresConnectionString,
            SchemaName = "nodes",
            IsMain = true
        };

        var database = new PostgresqlMessageStore(settings, new DurabilitySettings(),
            NpgsqlDataSource.Create(Servers.PostgresConnectionString),
            NullLogger<PostgresqlMessageStore>.Instance);

        await database.Admin.MigrateAsync();

        return database;
    }
}