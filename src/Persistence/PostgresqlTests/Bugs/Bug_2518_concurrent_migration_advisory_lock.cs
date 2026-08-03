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

    /// <summary>
    /// A lock id belonging to this test alone, deliberately <b>not</b>
    /// <see cref="DatabaseSettings.MigrationLockId"/>.
    ///
    /// <para>GH-3763. The advisory lock namespace is global to the Postgres server, and 4006 is the id
    /// every Wolverine migration takes (<c>MessageDatabase.Admin</c>) — while Bobcat runs
    /// PostgresqlTests across <b>three worker processes</b> against this one server. Any host
    /// bootstrapping in a sibling process holds 4006 for the length of its DDL, so the assertions
    /// below were racing every migration in a 470-test suite: the holder could fail to acquire, or
    /// another session could take the lock in the window after the release. That is the entirety of
    /// this test's flakiness — it cost CIPersistence its only retry of main run 30847233633.</para>
    ///
    /// <para>Nothing is lost by moving off 4006: what is under test is the mutual exclusion of the
    /// primitive, not the numeric value of the constant, and the constant is never asserted on.
    /// GH-2518's actual subject — that concurrent <c>MigrateAsync</c> calls serialize on the real
    /// lock id — is covered by <see cref="concurrent_migrate_async_calls_do_not_race_on_create_schema"/>,
    /// which still exercises the production path end to end.</para>
    /// </summary>
    private const int TestLockId = 25180001;

    [Fact]
    public async Task global_advisory_lock_is_exclusive_while_held()
    {
        // Verify the migration lock primitive itself works: while one connection holds
        // a lock id, another cannot acquire it.
        var lockId = TestLockId;

        await using var holder = new NpgsqlConnection(Servers.PostgresConnectionString);
        await holder.OpenAsync(TestContext.Current.CancellationToken);

        await using var contender = new NpgsqlConnection(Servers.PostgresConnectionString);
        await contender.OpenAsync(TestContext.Current.CancellationToken);

        var holderResult = await holder.TryGetGlobalLock(lockId, cancellation: TestContext.Current.CancellationToken);
        try
        {
            holderResult.ShouldBe(AttainLockResult.Success);

            var contenderResult = await contender.TryGetGlobalLock(lockId, cancellation: TestContext.Current.CancellationToken);
            contenderResult.Succeeded.ShouldBeFalse(
                "A second session must not be able to acquire the same advisory lock");
        }
        finally
        {
            await holder.ReleaseGlobalLock(lockId, cancellation: TestContext.Current.CancellationToken);
        }

        // After release, contender can acquire it
        var afterRelease = await contender.TryGetGlobalLock(lockId, cancellation: TestContext.Current.CancellationToken);
        try
        {
            afterRelease.ShouldBe(AttainLockResult.Success);
        }
        finally
        {
            await contender.ReleaseGlobalLock(lockId, cancellation: TestContext.Current.CancellationToken);
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
