using IntegrationTests;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Weasel.SqlServer;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;
using Wolverine.RDBMS.Sagas;
using Wolverine.SqlServer.Persistence;

namespace SqlServerTests.Agents;

public class node_persistence : NodePersistenceCompliance
{
    protected override async Task<IMessageStore> buildCleanMessageStore()
    {
        await using (var conn = new SqlConnection(Servers.SqlServerConnectionString))
        {
            await conn.OpenAsync();

            await conn.DropSchemaAsync("nodes");
        }

        var settings = new DatabaseSettings
        {
            ConnectionString = Servers.SqlServerConnectionString,
            SchemaName = "nodes",
            IsMain = true
        };

        var database = new SqlServerMessageStore(settings, new DurabilitySettings(),
            NullLogger<SqlServerMessageStore>.Instance, Array.Empty<SagaTableDefinition>());

        await database.Admin.MigrateAsync();

        return database;
    }
}