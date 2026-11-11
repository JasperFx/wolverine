using IntegrationTests;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using Shouldly;
using Wolverine;
using Wolverine.MySql;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;
using Wolverine.RDBMS.Sagas;

namespace MySqlTests;

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
            ConnectionString = Servers.MySqlConnectionString,
            SchemaName = SchemaName,
            Role = MessageStoreRole.Main
        };

        // No stale times, no capacity-aware assignment: nothing that reads these objects is turned on
        var durability = new DurabilitySettings
        {
            AlwaysProvisionOptionalSchemaObjects = alwaysProvisionOptionalSchemaObjects
        };

        await using var store = new MySqlMessageStore(settings, durability,
            MySqlDataSourceFactory.Create(Servers.MySqlConnectionString),
            NullLogger<MySqlMessageStore>.Instance, Array.Empty<SagaTableDefinition>());

        await store.Admin.MigrateAsync();
    }

    private static async Task<int> countAsync(string sql)
    {
        await using var conn = new MySqlConnection(Servers.MySqlConnectionString);
        await conn.OpenAsync(Ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(Ct));
    }

    private static Task<int> columnCountAsync(string table, string column) => countAsync(
        $"select count(*) from information_schema.columns where table_schema = '{SchemaName}' and table_name = '{table}' and column_name = '{column}'");

    private static Task<int> staleIndexCountAsync() => countAsync(
        $"select count(distinct index_name) from information_schema.statistics where table_schema = '{SchemaName}' and table_name = '{DatabaseConstants.OutgoingTable}' and index_name = 'idx_{DatabaseConstants.OutgoingTable}_stale'");

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
        await using (var conn = new MySqlConnection(Servers.MySqlConnectionString))
        {
            await conn.OpenAsync(Ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"DROP DATABASE IF EXISTS `{SchemaName}`";
            await cmd.ExecuteNonQueryAsync(Ct);
        }

        // The 6.x layout, which RestoreV6Defaults() puts back
        await migrateAsync(alwaysProvisionOptionalSchemaObjects: false);
        (await optionalObjectCountsAsync()).ShouldBe([0, 0, 0, 0]);

        // The 7.0 default
        await migrateAsync(alwaysProvisionOptionalSchemaObjects: true);
        (await optionalObjectCountsAsync()).ShouldBe([1, 1, 1, 1]);
    }
}
