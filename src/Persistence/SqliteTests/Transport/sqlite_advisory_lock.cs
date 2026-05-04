using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.Sqlite;

namespace SqliteTests.Transport;

[Collection("sqlite")]
public class sqlite_advisory_lock : SqliteContext, IAsyncLifetime
{
    private SqliteTestDatabase _db = null!;

    public Task InitializeAsync()
    {
        _db = Servers.CreateDatabase("sqlite_advisory_lock");
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _db.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task try_attain_is_idempotent()
    {
        // Regression test for the bug where calling TryAttainLockAsync twice for
        // the same lockId on the same instance returned false the second time
        // (because INSERT OR IGNORE no-ops on the existing row).
        using var host = await CreateHostAsync(_db.ConnectionString);
        var store = (SqliteMessageStore)host.Services.GetRequiredService<IMessageStore>();

        (await store.AdvisoryLock.TryAttainLockAsync(4242, default)).ShouldBeTrue();
        (await store.AdvisoryLock.TryAttainLockAsync(4242, default)).ShouldBeTrue();
        store.AdvisoryLock.HasLock(4242).ShouldBeTrue();

        await store.AdvisoryLock.ReleaseLockAsync(4242);
        store.AdvisoryLock.HasLock(4242).ShouldBeFalse();
    }

    [Fact]
    public async Task release_actually_deletes_the_row()
    {
        // The base ReleaseLockAsync was a no-op for SQLite (its doc comment
        // assumed session-scoped engine locks). The override now delegates to
        // SqliteAdvisoryLock which deletes the row.
        using var host = await CreateHostAsync(_db.ConnectionString);
        var store = (SqliteMessageStore)host.Services.GetRequiredService<IMessageStore>();

        await store.AdvisoryLock.TryAttainLockAsync(7777, default);
        await store.AdvisoryLock.ReleaseLockAsync(7777);

        await using var conn = new SqliteConnection(_db.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "select count(*) from wolverine_locks where lock_id = 7777";
        ((long)(await cmd.ExecuteScalarAsync())!).ShouldBe(0);
    }

    [Fact]
    public async Task stale_row_is_reaped_on_attempt()
    {
        // A holder that died without releasing leaves a row no peer would ever
        // clean up. The TTL sweep on TryAttainLockAsync deletes rows older than
        // the configured TTL before the INSERT OR IGNORE.
        using var host = await CreateHostAsync(_db.ConnectionString); // creates wolverine_locks

        await using (var seed = new SqliteConnection(_db.ConnectionString))
        {
            await seed.OpenAsync();
            await using var cmd = seed.CreateCommand();
            cmd.CommandText = "INSERT INTO wolverine_locks (lock_id, acquired_at) VALUES ($id, $when)";
            cmd.Parameters.AddWithValue("$id", 9001);
            // Pre-date by 10s; TTL is 1s in this test
            cmd.Parameters.AddWithValue("$when",
                DateTime.UtcNow.AddSeconds(-10).ToString("yyyy-MM-dd HH:mm:ss"));
            await cmd.ExecuteNonQueryAsync();
        }

        var dataSource = new Weasel.Sqlite.SqliteDataSource(_db.ConnectionString);
        await using var lockA = new SqliteAdvisoryLock(dataSource, NullLogger.Instance,
            "test", TimeSpan.FromSeconds(1));

        (await lockA.TryAttainLockAsync(9001, default)).ShouldBeTrue();
    }

    [Fact]
    public async Task live_holder_is_not_stolen_after_ttl_thanks_to_heartbeat()
    {
        // Holder A keeps re-attempting (as the production polling loops do).
        // The heartbeat advances acquired_at on every re-attempt, so even
        // after the TTL window has elapsed several times over, holder B
        // cannot acquire the lock.
        using var host = await CreateHostAsync(_db.ConnectionString);

        var dataSource = new Weasel.Sqlite.SqliteDataSource(_db.ConnectionString);
        await using var holderA = new SqliteAdvisoryLock(dataSource, NullLogger.Instance,
            "A", TimeSpan.FromSeconds(1));
        await using var holderB = new SqliteAdvisoryLock(dataSource, NullLogger.Instance,
            "B", TimeSpan.FromSeconds(1));

        (await holderA.TryAttainLockAsync(9100, default)).ShouldBeTrue();

        // Beat the heartbeat across more than 2× TTL while B repeatedly tries
        for (var i = 0; i < 6; i++)
        {
            await Task.Delay(500);
            (await holderA.TryAttainLockAsync(9100, default)).ShouldBeTrue(); // heartbeat tick
            (await holderB.TryAttainLockAsync(9100, default)).ShouldBeFalse(); // never steals
        }
    }

    [Fact]
    public async Task heartbeat_advances_acquired_at_on_reattempt()
    {
        using var host = await CreateHostAsync(_db.ConnectionString);
        var store = (SqliteMessageStore)host.Services.GetRequiredService<IMessageStore>();

        (await store.AdvisoryLock.TryAttainLockAsync(9200, default)).ShouldBeTrue();
        var firstAcquired = await readAcquiredAtAsync(_db.ConnectionString, 9200);

        await Task.Delay(TimeSpan.FromSeconds(1.2));

        (await store.AdvisoryLock.TryAttainLockAsync(9200, default)).ShouldBeTrue();
        var secondAcquired = await readAcquiredAtAsync(_db.ConnectionString, 9200);

        secondAcquired.ShouldBeGreaterThan(firstAcquired);

        await store.AdvisoryLock.ReleaseLockAsync(9200);
    }

    private static async Task<DateTime> readAcquiredAtAsync(string connectionString, int lockId)
    {
        await using var conn = new SqliteConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT acquired_at FROM wolverine_locks WHERE lock_id = $id";
        cmd.Parameters.AddWithValue("$id", lockId);
        var raw = (string)(await cmd.ExecuteScalarAsync())!;
        return DateTime.SpecifyKind(
            DateTime.ParseExact(raw, "yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
            DateTimeKind.Utc);
    }

    private static async Task<IHost> CreateHostAsync(string connectionString)
    {
        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithSqlite(connectionString);
                opts.Discovery.DisableConventionalDiscovery();
            })
            .StartAsync();
    }
}
