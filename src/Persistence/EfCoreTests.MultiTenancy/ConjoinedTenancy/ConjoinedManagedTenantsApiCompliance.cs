using IntegrationTests;
using JasperFx;
using JasperFx.MultiTenancy;
using JasperFx.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.Postgresql;
using Wolverine.SqlServer;
using Xunit;

namespace EfCoreTests.MultiTenancy.ConjoinedTenancy;

/// <summary>
///     GH-3537: the batch AddWolverineManagedTenantsAsync / RemoveWolverineManagedTenantsAsync
///     convenience API mirroring Marten's admin family.
/// </summary>
[Collection("multi-tenancy")]
public abstract class ConjoinedManagedTenantsApiCompliance : IAsyncLifetime
{
    private readonly DatabaseEngine _engine;
    protected IHost theHost = null!;

    protected ConjoinedManagedTenantsApiCompliance(DatabaseEngine engine)
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
                    opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "conjoined_admin_api");
                    opts.Services.AddDbContextWithWolverineManagedConjoinedTenancy<ConjoinedItemsDbContext>(
                        (builder, connectionString) => builder.UseNpgsql(connectionString.Value),
                        AutoCreate.CreateOrUpdate);
                }
                else
                {
                    opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, "conjoined_admin_api");
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
    }

    public async Task DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    [Fact]
    public async Task batch_add_then_remove_round_trips_through_the_registry()
    {
        var a = "api_" + Guid.NewGuid().ToString("N").Substring(0, 8);
        var b = "api_" + Guid.NewGuid().ToString("N").Substring(0, 8);

        var added = await theHost.AddWolverineManagedTenantsAsync<ConjoinedItemsDbContext>(a, b);
        added.Count.ShouldBe(2);

        var source = theHost.Services.GetRequiredService<IDynamicTenantSource<string>>();
        await source.RefreshAsync();
        var active = source.AllActiveByTenant().Select(x => x.TenantId).ToList();
        active.ShouldContain(a);
        active.ShouldContain(b);

        await theHost.RemoveWolverineManagedTenantsAsync<ConjoinedItemsDbContext>(a, b);

        await source.RefreshAsync();
        var afterRemoval = source.AllActiveByTenant().Select(x => x.TenantId).ToList();
        afterRemoval.ShouldNotContain(a);
        afterRemoval.ShouldNotContain(b);
    }
}

public class conjoined_managed_tenants_api_with_postgresql : ConjoinedManagedTenantsApiCompliance
{
    public conjoined_managed_tenants_api_with_postgresql() : base(DatabaseEngine.PostgreSQL)
    {
    }
}

public class conjoined_managed_tenants_api_with_sqlserver : ConjoinedManagedTenantsApiCompliance
{
    public conjoined_managed_tenants_api_with_sqlserver() : base(DatabaseEngine.SqlServer)
    {
    }
}
