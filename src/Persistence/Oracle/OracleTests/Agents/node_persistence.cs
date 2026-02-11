using IntegrationTests;
using Microsoft.Extensions.Logging.Abstractions;
using Oracle.ManagedDataAccess.Client;
using Weasel.Oracle;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Oracle;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;
using Wolverine.RDBMS.Sagas;

namespace OracleTests.Agents;

[Collection("oracle")]
public class node_persistence : NodePersistenceCompliance
{
    protected override async Task<IMessageStore> buildCleanMessageStore()
    {
        // Clean up Oracle schema objects
        await using var conn = new OracleConnection(Servers.OracleConnectionString);
        await conn.OpenAsync();
        try
        {
            // Drop and recreate the schema by clearing tables
            // Oracle doesn't have DROP SCHEMA CASCADE like PostgreSQL
        }
        finally
        {
            await conn.CloseAsync();
        }

        var dataSource = new OracleDataSource(Servers.OracleConnectionString);
        var settings = new DatabaseSettings
        {
            ConnectionString = Servers.OracleConnectionString,
            SchemaName = "WOLVERINE",
            Role = MessageStoreRole.Main
        };

        var database = new OracleMessageStore(settings, new DurabilitySettings(), dataSource,
            NullLogger<OracleMessageStore>.Instance, Array.Empty<SagaTableDefinition>());

        await database.Admin.RebuildAsync();

        return database;
    }
}
