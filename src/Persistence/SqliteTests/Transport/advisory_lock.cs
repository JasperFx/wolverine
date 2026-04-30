using Microsoft.Extensions.DependencyInjection;
using System.Reflection;
using Weasel.Core;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;
using Wolverine.Sqlite;

namespace SqliteTests.Transport;

[Collection("sqlite")]
public class advisory_lock : SqliteContext, IAsyncLifetime
{
    private SqliteTestDatabase _main = null!;
    private SqliteTestDatabase _green = null!;
    private IHost _host1 = null!;
    private IHost _host2 = null!;

    public async Task InitializeAsync()
    {
        _main = Servers.CreateDatabase("sqlite_store_lock_main");
        _green = Servers.CreateDatabase("sqlite_store_lock_green");
        _host1 = CreateHost();
        _host2 = CreateHost();
        await Task.WhenAll(_host1.StartAsync(), _host2.StartAsync());
    }

    public async Task DisposeAsync()
    {
        _host1.Dispose();
        _host2.Dispose();
        _main.Dispose();
        _green.Dispose();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("green")]
    public async Task should_not_attain_lock_already_attained_by_another_host(string? tenantId)
    {
        var advisoryLock1 = await GetAdvisoryLockAsync(_host1, tenantId);
        (await advisoryLock1.TryAttainLockAsync(69, default)).ShouldBeTrue();
        advisoryLock1.HasLock(69).ShouldBeTrue();

        var advisoryLock2 = await GetAdvisoryLockAsync(_host2, tenantId);
        (await advisoryLock2.TryAttainLockAsync(69, default)).ShouldBeFalse();
        advisoryLock2.HasLock(69).ShouldBeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("green")]
    public async Task should_attain_lock_released_by_another_host(string? tenantId)
    {
        var advisoryLock1 = await GetAdvisoryLockAsync(_host1, tenantId);
        (await advisoryLock1.TryAttainLockAsync(69, default)).ShouldBeTrue();
        advisoryLock1.HasLock(69).ShouldBeTrue();
        await advisoryLock1.ReleaseLockAsync(69);
        advisoryLock1.HasLock(69).ShouldBeFalse();

        var advisoryLock2 = await GetAdvisoryLockAsync(_host2, tenantId);

        (await advisoryLock2.TryAttainLockAsync(69, default)).ShouldBeTrue();
        advisoryLock2.HasLock(69).ShouldBeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("green")]
    public async Task should_attain_lock_not_attained_by_another_host(string? tenantId)
    {
        var advisoryLock1 = await GetAdvisoryLockAsync(_host1, tenantId);
        (await advisoryLock1.TryAttainLockAsync(69, default)).ShouldBeTrue();
        advisoryLock1.HasLock(69).ShouldBeTrue();

        var advisoryLock2 = await GetAdvisoryLockAsync(_host2, tenantId);
        (await advisoryLock2.TryAttainLockAsync(70, default)).ShouldBeTrue();
        advisoryLock2.HasLock(70).ShouldBeTrue();
    }

    [Fact]
    public async Task should_attain_lock_per_tenant()
    {
        var advisoryLock1 = await GetAdvisoryLockAsync(_host1);
        (await advisoryLock1.TryAttainLockAsync(69, default)).ShouldBeTrue();
        advisoryLock1.HasLock(69).ShouldBeTrue();

        var advisoryLock2 = await GetAdvisoryLockAsync(_host2, "green");
        (await advisoryLock2.TryAttainLockAsync(69, default)).ShouldBeTrue();
        advisoryLock2.HasLock(69).ShouldBeTrue();
    }

    [Fact]
    public async Task should_attain_lock_twice()
    {
        var advisoryLock1 = await GetAdvisoryLockAsync(_host1);
        (await advisoryLock1.TryAttainLockAsync(69, default)).ShouldBeTrue();
        advisoryLock1.HasLock(69).ShouldBeTrue();

        (await advisoryLock1.TryAttainLockAsync(69, default)).ShouldBeTrue();
        advisoryLock1.HasLock(69).ShouldBeTrue();
    }

    [Fact]
    public async Task should_attain_lock_when_previous_owner_is_disposed_without_releasing()
    {
        var advisoryLock1 = await GetAdvisoryLockAsync(_host1);
        (await advisoryLock1.TryAttainLockAsync(69, default)).ShouldBeTrue();

        _host1.Dispose();

        var advisoryLock2 = await GetAdvisoryLockAsync(_host2);
        (await advisoryLock2.TryAttainLockAsync(69, default)).ShouldBeTrue();
        advisoryLock2.HasLock(69).ShouldBeTrue();
    }

    [Fact]
    public async Task should_not_keep_migration_lock_after_applying_migration_on_start()
    {
        var dbSettings1 = _host1.Services.GetRequiredService<DatabaseSettings>();
        var dbSettings2 = _host2.Services.GetRequiredService<DatabaseSettings>();
        dbSettings1.MigrationLockId.ShouldBe(dbSettings2.MigrationLockId);
        var migrationLockId = dbSettings1.MigrationLockId;

        var advisoryLock1 = await GetAdvisoryLockAsync(_host1);
        var advisoryLock2 = await GetAdvisoryLockAsync(_host2);

        advisoryLock1.HasLock(migrationLockId).ShouldBeFalse();
        advisoryLock2.HasLock(migrationLockId).ShouldBeFalse();
    }

    [Fact]  // This test is for demonstration - not expected behavior
    public async Task should_not_attain_lock_when_previous_owner_crashes_without_releasing()
    {
        var store = await GetStoreAsync(_host1);
        await store.AdvisoryLock.TryAttainLockAsync(69, default);
        var prop = typeof(SqliteMessageStore).GetProperty(
            nameof(SqliteMessageStore.AdvisoryLock),
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        prop?.SetValue(store, null);
        _host1.Dispose(); // advisory lock can't be released because of the simulated crash

        var store2 = await GetStoreAsync(_host2);
        (await store2.AdvisoryLock.TryAttainLockAsync(69, default)).ShouldBeFalse(); // stale lock hurts
        store2.AdvisoryLock.HasLock(69).ShouldBeFalse();
    }

    private static async Task<SqliteMessageStore> GetStoreAsync(IHost host, string? tenantId = null)
    {
        var multitenantedStore = (MultiTenantedMessageStore)host.Services.GetRequiredService<IMessageStore>();
        var store = await multitenantedStore.GetDatabaseAsync(tenantId);
        return (SqliteMessageStore)store;
    }

    private static async Task<IAdvisoryLock> GetAdvisoryLockAsync(IHost host, string? tenantId = null)
    {
        var store = await GetStoreAsync(host, tenantId);
        return store.AdvisoryLock;
    }

    private IHost CreateHost()
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithSqlite(_main.ConnectionString)
                    .RegisterStaticTenants(tenants => tenants.Register("green", _green.ConnectionString));

                opts.Discovery.DisableConventionalDiscovery();
            })
            .Build();
    }
}
