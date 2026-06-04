using IntegrationTests;
using JasperFx.MultiTenancy;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Postgresql;
using Wolverine.RDBMS.MultiTenancy;

namespace PostgresqlTests;

// GH-3016: when UseMasterTableTenancy() is configured, the resulting MasterTenantSource must be
// resolvable from DI as IDynamicTenantSource<string> so store-agnostic admin consumers can drive
// the dynamic-tenancy lifecycle through the abstraction (no concrete-type sniffing).
//
// These resolve services from a *built* (not started) host on purpose: the registration is a
// config-time concern, and skipping StartAsync avoids tenant-database provisioning and the
// unrelated empty-master-table SeedDatabasesAsync path (filed separately).
public class master_table_tenancy_di_registration : PostgresqlContext
{
    [Fact]
    public void master_tenant_source_is_registered_as_dynamic_tenant_source()
    {
        using var host = Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "mt_di_3016")
                    .UseMasterTableTenancy(tenants =>
                    {
                        // The target is irrelevant to this DI-registration assertion; a seeded tenant
                        // keeps the master-table boot path off the empty-table SeedDatabasesAsync branch.
                        tenants.Register("tenant1", Servers.PostgresConnectionString);
                    });
            })
            .Build();

        var sources = host.Services.GetServices<IDynamicTenantSource<string>>().ToArray();

        sources.ShouldNotBeEmpty();
        sources[0].ShouldBeOfType<MasterTenantSource>();
    }

    [Fact]
    public void no_dynamic_tenant_source_registered_without_master_table_tenancy()
    {
        using var host = Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "mt_di_3016_negative");
            })
            .Build();

        // A plain Postgres-backed host exposes no dynamic tenant source — graceful no-op for
        // abstraction-agnostic admin consumers.
        host.Services.GetServices<IDynamicTenantSource<string>>().ShouldBeEmpty();
    }

    [Fact]
    public async Task starts_cleanly_with_an_empty_master_tenant_table()
    {
        // GH-3019: an empty master tenant table (no seeded tenants) must not throw
        // "CommandText property has not been initialized" out of SeedDatabasesAsync during startup.
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "mt_empty_seed_3019")
                    .UseMasterTableTenancy(_ => { }); // intentionally no seeded tenants
                opts.Services.AddResourceSetupOnStartup();
            })
            .StartAsync();

        // Reaching a started host without throwing is the assertion; the registration still lights up.
        host.Services.GetServices<IDynamicTenantSource<string>>().ShouldNotBeEmpty();
    }
}
