using IntegrationTests;
using JasperFx;
using JasperFx.Descriptors;
using JasperFx.Events;
using JasperFx.MultiTenancy;
using JasperFx.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.EntityFrameworkCore.Internals;
using Wolverine.Postgresql;
using Wolverine.SqlServer;

namespace EfCoreTests.MultiTenancy.ConjoinedTenancy;

/// <summary>
///     GH-3536: a conjoined EF Core context must be distinguishable from a plain single-DB
///     context in its DbContextUsage descriptor — a "Conjoined" tenancy style, DynamicMultiple
///     cardinality, and the tenant ids from the wolverine_tenants registry surfaced on the single
///     shared-database descriptor — so CritterWatch's descriptor-driven UI can gate/badge it.
/// </summary>
[Collection("multi-tenancy")]
public abstract class ConjoinedDescriptorCompliance : IAsyncLifetime
{
    private readonly DatabaseEngine _engine;
    protected IHost theHost = null!;
    protected IDynamicTenantSource<string> theSource = null!;

    protected ConjoinedDescriptorCompliance(DatabaseEngine engine)
    {
        _engine = engine;
    }

    public async Task InitializeAsync()
    {
        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<ConjoinedItemHandler>();

                if (_engine == DatabaseEngine.PostgreSQL)
                {
                    opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "conjoined_descriptor");
                    opts.Services.AddDbContextWithWolverineManagedConjoinedTenancy<ConjoinedItemsDbContext>(
                        (builder, connectionString) => builder.UseNpgsql(connectionString.Value),
                        AutoCreate.CreateOrUpdate);
                }
                else
                {
                    opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, "conjoined_descriptor");
                    opts.Services.AddDbContextWithWolverineManagedConjoinedTenancy<ConjoinedItemsDbContext>(
                        (builder, connectionString) => builder.UseSqlServer(connectionString.Value),
                        AutoCreate.CreateOrUpdate);
                }

                opts.UseEntityFrameworkCoreTransactions();
                opts.UseEntityFrameworkCoreWolverineManagedMigrations();
                opts.Policies.AutoApplyTransactions();
                opts.Services.AddResourceSetupOnStartup();
                opts.PublishAllMessages().Locally();
            }).StartAsync();

        theSource = theHost.Services.GetRequiredService<IDynamicTenantSource<string>>();
    }

    public async Task DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    private async Task<DbContextUsage> createUsageAsync()
    {
        var source = theHost.Services.GetServices<IDbContextUsageSource>()
            .Single(x => x.Subject == new Uri("efcore://ConjoinedItemsDbContext"));

        var usage = await source.TryCreateUsage(CancellationToken.None);
        usage.ShouldNotBeNull();
        return usage!;
    }

    [Fact]
    public async Task conjoined_context_advertises_conjoined_tenancy_style_and_dynamic_multiple_cardinality()
    {
        var usage = await createUsageAsync();

        // The whole point of GH-3536: not "Single".
        usage.TenancyStyle.ShouldBe("Conjoined");
        usage.Database.Cardinality.ShouldBe(DatabaseCardinality.DynamicMultiple);

        // Conjoined is one physical database, so there is exactly one shared descriptor.
        usage.Database.Databases.Count.ShouldBe(1);
    }

    [Fact]
    public async Task conjoined_descriptor_surfaces_registry_tenant_ids()
    {
        var tenantA = "descriptor_" + Guid.NewGuid().ToString("N").Substring(0, 8);
        var tenantB = "descriptor_" + Guid.NewGuid().ToString("N").Substring(0, 8);

        await theSource.AddTenantAsync(tenantA, CancellationToken.None);
        await theSource.AddTenantAsync(tenantB, CancellationToken.None);

        var usage = await createUsageAsync();

        // Tenant ids from the wolverine_tenants registry ride on the single shared-database
        // descriptor rather than fanning out into per-tenant database entries.
        // Tenant ids are stored lower-cased by the registry; the ids created above are already
        // lower-case, so a direct membership check is exact.
        var tenantIds = usage.Database.MainDatabase!.TenantIds;
        tenantIds.ShouldContain(tenantA);
        tenantIds.ShouldContain(tenantB);

        await theSource.RemoveTenantAsync(tenantA);
        await theSource.RemoveTenantAsync(tenantB);
    }
}

public class conjoined_descriptor_with_postgresql : ConjoinedDescriptorCompliance
{
    public conjoined_descriptor_with_postgresql() : base(DatabaseEngine.PostgreSQL)
    {
    }
}

public class conjoined_descriptor_with_sqlserver : ConjoinedDescriptorCompliance
{
    public conjoined_descriptor_with_sqlserver() : base(DatabaseEngine.SqlServer)
    {
    }
}
