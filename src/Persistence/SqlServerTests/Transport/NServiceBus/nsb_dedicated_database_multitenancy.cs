using IntegrationTests;
using JasperFx.Resources;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Weasel.Core;
using Wolverine;
using Wolverine.SqlServer;
using Wolverine.SqlServer.Transport.NServiceBus;
using Xunit;

namespace SqlServerTests.Transport.NServiceBus;

// GH: the NServiceBus SQL Server interop transport can be pinned to one dedicated database via
// UseNServiceBusSqlServerInterop(connectionString: ...), decoupled from Wolverine's multi-tenanted
// (database-per-tenant) message storage. The transport must own its queue tables on that single
// database ONLY — never replicated across tenant databases (mirrors simonfox/wolverine.spike).
public class nsb_dedicated_database_multitenancy : IAsyncLifetime
{
    private string _mainCs = null!;
    private string _t1Cs = null!;
    private string _t2Cs = null!;
    private string _dedicatedCs = null!;
    private IHost _host = null!;
    private readonly string _queue = "dedicated_nsb_" + Guid.NewGuid().ToString("N")[..8];

    public async Task InitializeAsync()
    {
        _mainCs = await NsbMtDb.CreateDb("nsb_ded_main");
        _t1Cs = await NsbMtDb.CreateDb("nsb_ded_t1");
        _t2Cs = await NsbMtDb.CreateDb("nsb_ded_t2");
        _dedicatedCs = await NsbMtDb.CreateDb("nsb_ded_shared");

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // Native transport + persistence on the main database.
                opts.UseSqlServerPersistenceAndTransport(_mainCs);

                // Multi-tenanted storage: a database per tenant.
                opts.PersistMessagesWithSqlServer(_mainCs)
                    .UseMasterTableTenancy(t =>
                    {
                        t.Register("tenant_one", _t1Cs);
                        t.Register("tenant_two", _t2Cs);
                    });

                // NServiceBus interop pinned to one dedicated shared database, NOT the tenanted storage.
                opts.UseNServiceBusSqlServerInterop(autoProvision: true, connectionString: _dedicatedCs);

                opts.PublishMessage<ReproPing>().ToNServiceBusSqlServerQueue(_queue);

                opts.Services.AddResourceSetupOnStartup();
                opts.Discovery.DisableConventionalDiscovery();
            }).StartAsync();
    }

    [Fact]
    public async Task queue_table_is_provisioned_only_in_the_dedicated_database()
    {
        (await NsbMtDb.TableExists(_dedicatedCs, _queue)).ShouldBeTrue("queue table must exist in the dedicated NSB database");

        (await NsbMtDb.TableExists(_mainCs, _queue)).ShouldBeFalse("queue table must NOT be in the storage main database");
        (await NsbMtDb.TableExists(_t1Cs, _queue)).ShouldBeFalse("queue table must NOT be in tenant_one's database");
        (await NsbMtDb.TableExists(_t2Cs, _queue)).ShouldBeFalse("queue table must NOT be in tenant_two's database");
    }

    [Fact]
    public async Task sending_under_a_tenant_writes_to_the_dedicated_database_only()
    {
        await _host.MessageBus().PublishAsync(new ReproPing(Guid.NewGuid()),
            new DeliveryOptions { TenantId = "tenant_one" });

        // Buffered interop send — poll briefly for the row to land in the dedicated database.
        long dedicated = 0;
        for (var i = 0; i < 40 && dedicated == 0; i++)
        {
            dedicated = await RowCount(_dedicatedCs, _queue);
            if (dedicated == 0) await Task.Delay(100);
        }

        dedicated.ShouldBe(1);

        // And nowhere else — the tenant's own database never receives an interop queue table.
        (await NsbMtDb.TableExists(_t1Cs, _queue)).ShouldBeFalse();
        (await NsbMtDb.TableExists(_mainCs, _queue)).ShouldBeFalse();
    }

    private static async Task<long> RowCount(string cs, string table)
    {
        if (!await NsbMtDb.TableExists(cs, table)) return 0;
        await using var conn = new SqlConnection(cs);
        await conn.OpenAsync();
        var cmd = conn.CreateCommand($"SELECT COUNT(*) FROM [dbo].[{table}]");
        return (int)(await cmd.ExecuteScalarAsync())!;
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }
}

// Locks in the DEFAULT behavior (no explicit connection string): under database-per-tenant storage the NServiceBus
// interop transport binds to the Main store only and is NEVER replicated into tenant databases.
public class nsb_default_database_multitenancy : IAsyncLifetime
{
    private string _mainCs = null!;
    private string _t1Cs = null!;
    private IHost _host = null!;
    private readonly string _queue = "default_nsb_" + Guid.NewGuid().ToString("N")[..8];

    public async Task InitializeAsync()
    {
        _mainCs = await NsbMtDb.CreateDb("nsb_def_main");
        _t1Cs = await NsbMtDb.CreateDb("nsb_def_t1");

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseSqlServerPersistenceAndTransport(_mainCs);
                opts.PersistMessagesWithSqlServer(_mainCs)
                    .UseMasterTableTenancy(t => t.Register("tenant_one", _t1Cs));

                // No explicit connection string — falls back to the Main store.
                opts.UseNServiceBusSqlServerInterop(autoProvision: true);
                opts.PublishMessage<ReproPing>().ToNServiceBusSqlServerQueue(_queue);

                opts.Services.AddResourceSetupOnStartup();
                opts.Discovery.DisableConventionalDiscovery();
            }).StartAsync();
    }

    [Fact]
    public async Task binds_to_main_store_not_tenant_databases()
    {
        (await NsbMtDb.TableExists(_mainCs, _queue)).ShouldBeTrue("default binding is the Main message store");
        (await NsbMtDb.TableExists(_t1Cs, _queue)).ShouldBeFalse("must NOT be replicated into a tenant database");
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }
}

public record ReproPing(Guid Id);

internal static class NsbMtDb
{
    public static async Task<bool> TableExists(string cs, string table)
    {
        await using var conn = new SqlConnection(cs);
        await conn.OpenAsync();
        var cmd = conn.CreateCommand("SELECT CASE WHEN OBJECT_ID(@n, N'U') IS NULL THEN 0 ELSE 1 END")
            .With("n", $"[dbo].[{table}]");
        return (int)(await cmd.ExecuteScalarAsync())! == 1;
    }

    public static async Task<string> CreateDb(string name)
    {
        var builder = new SqlConnectionStringBuilder(Servers.SqlServerConnectionString);
        await using (var conn = new SqlConnection(builder.ConnectionString))
        {
            await conn.OpenAsync();
            var exists = await conn.CreateCommand($"SELECT DB_ID('{name}')").ExecuteScalarAsync();
            if (exists == null || exists == DBNull.Value)
            {
                await conn.CreateCommand($"CREATE DATABASE [{name}]").ExecuteNonQueryAsync();
            }
        }

        builder.InitialCatalog = name;
        return builder.ConnectionString;
    }
}
