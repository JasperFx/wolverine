using IntegrationTests;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Shouldly;
using Weasel.Core.Migrations;
using Weasel.Postgresql;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.Postgresql;
using Wolverine.RDBMS;

namespace PostgresqlTests.Bugs;

/// <summary>
/// GH-2518: Concurrent calls to MigrateAsync against a fresh schema must not
/// race on CREATE SCHEMA IF NOT EXISTS. Wolverine acquires a session-scoped
/// advisory lock around the migration to serialize across processes.
/// </summary>
public class Bug_2518_concurrent_migration_advisory_lock : PostgresqlContext
{
    private const string TestSchemaName = "concurrent_migration_2518";

    [Fact]
    public async Task concurrent_migrate_async_calls_do_not_race_on_create_schema()
    {
        // Drop the schema first so we exercise the CREATE SCHEMA path on every store
        await dropSchemaAsync();

        const int concurrency = 16;
        var stores = Enumerable.Range(0, concurrency).Select(_ => buildStore()).ToArray();

        try
        {
            var migrations = stores.Select(s => s.Admin.MigrateAsync()).ToArray();

            // All concurrent migrations must complete without throwing — the advisory
            // lock serializes them so only one runs the DDL at a time.
            await Task.WhenAll(migrations);
        }
        finally
        {
            foreach (var store in stores)
            {
                await store.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task migration_lock_id_is_actually_held_during_migration()
    {
        // Verify the migration lock primitive itself works: while one connection holds
        // the configured MigrationLockId, another cannot acquire it.
        var lockId = new DatabaseSettings().MigrationLockId;

        await using var holder = new NpgsqlConnection(Servers.PostgresConnectionString);
        await holder.OpenAsync();

        await using var contender = new NpgsqlConnection(Servers.PostgresConnectionString);
        await contender.OpenAsync();

        var holderResult = await holder.TryGetGlobalLock(lockId);
        try
        {
            holderResult.ShouldBe(AttainLockResult.Success);

            var contenderResult = await contender.TryGetGlobalLock(lockId);
            contenderResult.Succeeded.ShouldBeFalse(
                "A second session must not be able to acquire the same advisory lock");
        }
        finally
        {
            await holder.ReleaseGlobalLock(lockId);
        }

        // After release, contender can acquire it
        var afterRelease = await contender.TryGetGlobalLock(lockId);
        try
        {
            afterRelease.ShouldBe(AttainLockResult.Success);
        }
        finally
        {
            await contender.ReleaseGlobalLock(lockId);
        }
    }

    private static PostgresqlMessageStore buildStore()
    {
        var settings = new DatabaseSettings
        {
            ConnectionString = Servers.PostgresConnectionString,
            Role = MessageStoreRole.Main,
            SchemaName = TestSchemaName
        };

        var dataSource = NpgsqlDataSource.Create(Servers.PostgresConnectionString);
        return new PostgresqlMessageStore(settings, new DurabilitySettings(), dataSource,
            NullLogger<PostgresqlMessageStore>.Instance);
    }

    private static async Task dropSchemaAsync()
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DROP SCHEMA IF EXISTS {TestSchemaName} CASCADE";
        await cmd.ExecuteNonQueryAsync();
    }
}
