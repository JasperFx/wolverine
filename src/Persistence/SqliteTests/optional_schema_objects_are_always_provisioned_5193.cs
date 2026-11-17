using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Weasel.Sqlite;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;
using Wolverine.Sqlite;

namespace SqliteTests;

/// <summary>
/// GH-5193. Since 7.0 the message store always creates the incoming and outgoing <c>timestamp</c> columns, the
/// outgoing stale index and <c>wolverine_nodes.load_factor</c>, whatever InboxStaleTime, OutboxStaleTime and
/// CapacityAwareAssignment say. A database built with the 6.x layout gains all four on its first migration.
/// </summary>
public class optional_schema_objects_are_always_provisioned_5193 : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly SqliteTestDatabase theDatabase =
        Servers.CreateDatabase(nameof(optional_schema_objects_are_always_provisioned_5193));

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        theDatabase.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task migrateAsync(bool alwaysProvisionOptionalSchemaObjects)
    {
        var settings = new DatabaseSettings
        {
            ConnectionString = theDatabase.ConnectionString,
            SchemaName = "main",
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

        await using var store = new SqliteMessageStore(settings, durability,
            new SqliteDataSource(theDatabase.ConnectionString),
            NullLogger<SqliteMessageStore>.Instance);

        await store.Admin.MigrateAsync();
    }

    private async Task<long> countAsync(string sql)
    {
        await using var conn = new SqliteConnection(theDatabase.ConnectionString);
        await conn.OpenAsync(Ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(Ct));
    }

    private Task<long> columnCountAsync(string table, string column) => countAsync(
        $"select count(*) from pragma_table_info('{table}') where name = '{column}'");

    private Task<long> staleIndexCountAsync() => countAsync(
        $"select count(*) from sqlite_master where type = 'index' and name = 'idx_{DatabaseConstants.OutgoingTable}_stale'");

    private async Task<long[]> optionalObjectCountsAsync() =>
    [
        await columnCountAsync(DatabaseConstants.IncomingTable, DatabaseConstants.Timestamp),
        await columnCountAsync(DatabaseConstants.OutgoingTable, DatabaseConstants.Timestamp),
        await staleIndexCountAsync(),
        await columnCountAsync(DatabaseConstants.NodeTableName, DatabaseConstants.LoadFactor)
    ];

    [Fact]
    public async Task a_6x_schema_gains_every_optional_object_on_its_first_7_0_migration()
    {
        // The 6.x layout, which RestoreV6Defaults() puts back
        await migrateAsync(alwaysProvisionOptionalSchemaObjects: false);
        (await optionalObjectCountsAsync()).ShouldBe([0L, 0L, 0L, 0L]);

        // The 7.0 default
        await migrateAsync(alwaysProvisionOptionalSchemaObjects: true);
        (await optionalObjectCountsAsync()).ShouldBe([1L, 1L, 1L, 1L]);
    }
}
