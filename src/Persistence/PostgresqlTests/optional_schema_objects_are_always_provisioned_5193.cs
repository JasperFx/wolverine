using IntegrationTests;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Shouldly;
using Weasel.Postgresql;
using Wolverine;
using Wolverine.Postgresql;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;

namespace PostgresqlTests;

/// <summary>
/// GH-5193. Since 7.0 the message store always creates the incoming and outgoing <c>timestamp</c> columns, the
/// outgoing stale index and <c>wolverine_nodes.load_factor</c>, whatever InboxStaleTime, OutboxStaleTime and
/// CapacityAwareAssignment say. A database built with the 6.x layout gains all four on its first migration.
/// </summary>
public class optional_schema_objects_are_always_provisioned_5193 : PostgresqlContext
{
    private const string SchemaName = "optional_schema_5193";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task migrateAsync(bool alwaysProvisionOptionalSchemaObjects)
    {
        var settings = new DatabaseSettings
        {
            ConnectionString = Servers.PostgresConnectionString,
            SchemaName = SchemaName,
            Role = MessageStoreRole.Main
        };

        // No stale times, no capacity-aware assignment: nothing that reads these objects is turned on
        var durability = new DurabilitySettings
        {
            AlwaysProvisionOptionalSchemaObjects = alwaysProvisionOptionalSchemaObjects,

            // GH-5519: both default to 1 hour in 7.0, and either one provisions its timestamp column on its own
            InboxStaleTime = null,
            OutboxStaleTime = null
        };

        await using var store = new PostgresqlMessageStore(settings, durability,
            NpgsqlDataSource.Create(Servers.PostgresConnectionString),
            NullLogger<PostgresqlMessageStore>.Instance);

        await store.Admin.MigrateAsync();
    }

    private static async Task<long> countAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync(Ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(Ct));
    }

    private static Task<long> columnCountAsync(string table, string column) => countAsync(
        $"select count(*) from information_schema.columns where table_schema = '{SchemaName}' and table_name = '{table}' and column_name = '{column}'");

    private static Task<long> staleIndexCountAsync() => countAsync(
        $"select count(*) from pg_indexes where schemaname = '{SchemaName}' and indexname = 'idx_{DatabaseConstants.OutgoingTable}_stale'");

    private static async Task<long[]> optionalObjectCountsAsync() =>
    [
        await columnCountAsync(DatabaseConstants.IncomingTable, DatabaseConstants.Timestamp),
        await columnCountAsync(DatabaseConstants.OutgoingTable, DatabaseConstants.Timestamp),
        await staleIndexCountAsync(),
        await columnCountAsync(DatabaseConstants.NodeTableName, DatabaseConstants.LoadFactor)
    ];

    [Fact]
    public async Task a_6x_schema_gains_every_optional_object_on_its_first_7_0_migration()
    {
        await using (var conn = new NpgsqlConnection(Servers.PostgresConnectionString))
        {
            await conn.OpenAsync(Ct);
            await conn.DropSchemaAsync(SchemaName, ct: Ct);
        }

        // The 6.x layout, which RestoreV6Defaults() puts back
        await migrateAsync(alwaysProvisionOptionalSchemaObjects: false);
        (await optionalObjectCountsAsync()).ShouldBe([0L, 0L, 0L, 0L]);

        // The 7.0 default
        await migrateAsync(alwaysProvisionOptionalSchemaObjects: true);
        (await optionalObjectCountsAsync()).ShouldBe([1L, 1L, 1L, 1L]);
    }
}
