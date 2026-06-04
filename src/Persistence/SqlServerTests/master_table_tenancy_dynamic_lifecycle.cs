using IntegrationTests;
using JasperFx.MultiTenancy;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.RDBMS.MultiTenancy;
using Wolverine.SqlServer;

namespace SqlServerTests;

// GH-3023: master-table multi-tenancy on SqlServer. The shared tenant-table queries used
// PostgreSQL-only SQL (boolean `false`/`true` literals + ON CONFLICT upsert) that SqlServer
// rejects ("Invalid column name 'false'"), so this whole path was broken on SqlServer. These
// exercise the read + write + disable/enable paths end-to-end through the (now DI-registered,
// parity with GH-3016) IDynamicTenantSource<string> abstraction.
public class master_table_tenancy_dynamic_lifecycle
{
    [Fact]
    public async Task master_tenant_source_is_registered_as_dynamic_tenant_source()
    {
        using var host = Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, "mt_di_3023")
                    .UseMasterTableTenancy(tenants => tenants.Register("seed", Servers.SqlServerConnectionString));
            })
            .Build();

        host.Services.GetServices<IDynamicTenantSource<string>>().OfType<MasterTenantSource>()
            .ShouldHaveSingleItem();
    }

    [Fact]
    public async Task dynamic_tenant_lifecycle_round_trip()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, "mt_lifecycle_3023")
                    .UseMasterTableTenancy(_ => { });
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        var source = host.Services.GetServices<IDynamicTenantSource<string>>()
            .OfType<MasterTenantSource>().Single();

        var tenant = "life-" + Guid.NewGuid().ToString("N")[..8];
        var conn = Servers.SqlServerConnectionString;

        // Add — AddTenantRecordAsync (portable delete-then-insert; was PostgreSQL-only ON CONFLICT + `false` literal)
        await source.AddTenantAsync(tenant, conn);

        // Read — LoadAllTenantConnectionStrings (was `where disabled = false`)
        await source.RefreshAsync();
        source.AllActiveByTenant().ShouldContain(a => a.TenantId == tenant);

        // Disable — shows up in the disabled list (LoadDisabledTenantIdsAsync; was `where disabled = true`)
        await source.DisableTenantAsync(tenant);
        (await source.AllDisabledAsync()).ShouldContain(tenant);

        // Enable — no longer disabled
        await source.EnableTenantAsync(tenant);
        (await source.AllDisabledAsync()).ShouldNotContain(tenant);

        // Re-add must not throw (upsert path)
        await source.AddTenantAsync(tenant, conn);

        // Remove
        await source.RemoveTenantAsync(tenant);
    }
}
