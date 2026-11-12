using IntegrationTests;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Weasel.SqlServer;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;
using Wolverine.RDBMS.Sagas;
using Wolverine.SqlServer.Persistence;
using Xunit;

namespace SqlServerTests;

/// <summary>
/// GH-5193. Since 7.0 the message store always creates the incoming and outgoing <c>timestamp</c> columns, the
/// outgoing stale index and <c>wolverine_nodes.load_factor</c>, whatever InboxStaleTime, OutboxStaleTime and
/// CapacityAwareAssignment say. A database built with the 6.x layout gains all four on its first migration.
/// </summary>
public class optional_schema_objects_are_always_provisioned_5193
{
    private const string SchemaName = "optional_schema_5193";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task migrateAsync(bool alwaysProvisionOptionalSchemaObjects)
    {
        var settings = new DatabaseSettings
        {
            ConnectionString = Servers.SqlServerConnectionString,
            SchemaName = SchemaName,
            Role = MessageStoreRole.Main
        };

        // No stale times, no capacity-aware assignment: nothing that reads these objects is turned on
        var durability = new DurabilitySettings
        {
            AlwaysProvisionOptionalSchemaObjects = alwaysProvisionOptionalSchemaObjects,
            // Both stale times default on since 7.0 (GH-5519), which would provision the timestamp columns
            // on their own; turn them off so only AlwaysProvisionOptionalSchemaObjects decides
            InboxStaleTime = null,
            OutboxStaleTime = null
        };

        await using var store = new SqlServerMessageStore(settings, durability,
            NullLogger<SqlServerMessageStore>.Instance, Array.Empty<SagaTableDefinition>());

        await store.Admin.MigrateAsync();
    }

    private static async Task<int> countAsync(string sql)
    {
        await using var conn = new SqlConnection(Servers.SqlServerConnectionString);
        await conn.OpenAsync(Ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(Ct));
    }

    private static Task<int> columnCountAsync(string table, string column) => countAsync(
        $"select count(*) from information_schema.columns where table_schema = '{SchemaName}' and table_name = '{table}' and column_name = '{column}'");

    private static Task<int> staleIndexCountAsync() => countAsync(
        $"select count(*) from sys.indexes i join sys.tables t on i.object_id = t.object_id join sys.schemas s on t.schema_id = s.schema_id where s.name = '{SchemaName}' and i.name = 'idx_{DatabaseConstants.OutgoingTable}_stale'");

    private static async Task<int[]> optionalObjectCountsAsync() =>
    [
        await columnCountAsync(DatabaseConstants.IncomingTable, DatabaseConstants.Timestamp),
        await columnCountAsync(DatabaseConstants.OutgoingTable, DatabaseConstants.Timestamp),
        await staleIndexCountAsync(),
        await columnCountAsync(DatabaseConstants.NodeTableName, DatabaseConstants.LoadFactor)
    ];

    [Fact]
    public async Task a_6x_schema_gains_every_optional_object_on_its_first_7_0_migration()
    {
        await using (var conn = new SqlConnection(Servers.SqlServerConnectionString))
        {
            await conn.OpenAsync(Ct);
            await conn.DropSchemaAsync(SchemaName, Ct);
        }

        // The 6.x layout, which RestoreV6Defaults() puts back
        await migrateAsync(alwaysProvisionOptionalSchemaObjects: false);
        (await optionalObjectCountsAsync()).ShouldBe([0, 0, 0, 0]);

        // The 7.0 default
        await migrateAsync(alwaysProvisionOptionalSchemaObjects: true);
        (await optionalObjectCountsAsync()).ShouldBe([1, 1, 1, 1]);
    }
}
