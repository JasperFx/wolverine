using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.Sqlite;

namespace SqliteTests.Transport;

[Collection("sqlite")]
public class sqlite_migration_lock : SqliteContext, IAsyncLifetime
{
    private SqliteTestDatabase _db = null!;

    public Task InitializeAsync()
    {
        _db = Servers.CreateDatabase("sqlite_migration_lock");
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _db.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task migrate_async_does_not_leave_a_row_in_wolverine_locks()
    {
        // The whole point of switching the migration lock to BEGIN EXCLUSIVE: it
        // can't deposit rows in wolverine_locks (because the table is created by
        // the same migration). After startup the table exists but holds no rows
        // for the migration lockId.
        using var host = await CreateHostAsync(_db.ConnectionString);

        var store = (SqliteMessageStore)host.Services.GetRequiredService<IMessageStore>();
        var migrationLockId = store.Settings.MigrationLockId;

        await using var conn = new SqliteConnection(_db.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "select count(*) from wolverine_locks where lock_id = $id";
        cmd.Parameters.AddWithValue("$id", migrationLockId);
        var count = (long)(await cmd.ExecuteScalarAsync())!;
        count.ShouldBe(0);
    }

    [Fact]
    public async Task two_hosts_can_start_concurrently_against_the_same_file()
    {
        // Without the BEGIN EXCLUSIVE migration lock, the second startup hits
        // the chicken-and-egg (wolverine_locks doesn't exist yet) and burns
        // ~5.5s of failed lock retries. With BEGIN EXCLUSIVE, one waits, the
        // other proceeds, and both reach Started without errors.
        var startup = Task.WhenAll(
            CreateHostAsync(_db.ConnectionString),
            CreateHostAsync(_db.ConnectionString));

        var hosts = await startup.WaitAsync(TimeSpan.FromSeconds(15));
        try
        {
            hosts.ShouldNotBeNull();
            hosts.Length.ShouldBe(2);
        }
        finally
        {
            foreach (var h in hosts) await h.StopAsync();
            foreach (var h in hosts) h.Dispose();
        }
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
