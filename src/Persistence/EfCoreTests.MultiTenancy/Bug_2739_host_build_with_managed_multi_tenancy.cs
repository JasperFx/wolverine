using IntegrationTests;
using JasperFx;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SharedPersistenceModels.Items;
using Shouldly;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.SqlServer;
using Xunit;

namespace EfCoreTests.MultiTenancy;

/// <summary>
/// Regression guard for wolverine#2739.
///
/// On Wolverine <b>5.39.0</b>, an app that called
/// <c>AddDbContextWithWolverineManagedMultiTenancy&lt;T&gt;(...)</c> threw on
/// <c>app.Run()</c>:
///
/// <code>
///   System.InvalidOperationException:
///     As of Wolverine 3.0, it's no longer supported to alter IoC service
///     registrations through Wolverine extensions that are themselves
///     registered in the IoC container
///   at Wolverine.WolverineOptions.ApplyExtensions(IWolverineExtension[] extensions)
///   at Wolverine.HostBuilderExtensions...
/// </code>
///
/// The bug was introduced when <c>EntityFrameworkCoreBackedPersistence&lt;T&gt;.Configure</c>
/// added an <c>options.Services.AddSingleton&lt;ISagaStoreDiagnostics&gt;(...)</c>
/// call at host-build time — past the point where <c>IServiceCollection</c>
/// is still mutable. The fix (committed alongside this test on the 5.0
/// branch) moves that registration to the four <c>IServiceCollection</c>
/// extension entry-points in <c>WolverineEntityCoreExtensions</c>, where
/// Services is still mutable, gated by a private marker type so multiple
/// entry-point calls produce a single registration without ever using
/// <c>TryAddSingleton</c> on the additive <c>ISagaStoreDiagnostics</c>
/// fan-out slot.
///
/// The test runs the host-build path with <c>AutoCreate.None</c> so no
/// per-tenant schema is provisioned — the bug fires during the first
/// singleton resolution at <c>StartAsync</c>, well before any database
/// I/O. The single master <c>PersistMessagesWithSqlServer</c> call needs
/// a reachable SQL Server because the master message-store schema gets
/// provisioned at startup, but no actual tenant database is required.
/// </summary>
[Collection("multi-tenancy")]
public class Bug_2739_host_build_with_managed_multi_tenancy
{
    [Fact]
    public async Task host_build_does_not_throw_with_AddDbContextWithWolverineManagedMultiTenancy()
    {
        // Use HostApplicationBuilder (.NET 8+) rather than the older
        // Host.CreateDefaultBuilder() pattern. WebApplicationBuilder and
        // HostApplicationBuilder both make the underlying IServiceCollection
        // read-only after Build, which is the condition the bug fires
        // under — Host.CreateDefaultBuilder() does NOT freeze the
        // collection, and won't reproduce the issue.
        //
        // The user's report (#2739) was on an ASP.NET Core app built
        // with WebApplication.CreateBuilder; HostApplicationBuilder
        // gives the same freeze semantics without pulling in
        // Microsoft.AspNetCore.App as a FrameworkReference here.
        var builder = Host.CreateApplicationBuilder();
        builder.UseWolverine(opts =>
        {
            opts.Durability.Mode = DurabilityMode.Solo;

            opts.PersistMessagesWithSqlServer(
                    Servers.SqlServerConnectionString,
                    "bug2739_master")
                .RegisterStaticTenants(tenants =>
                {
                    // A single static tenant is enough — the bug fires
                    // during the EFCore extension's Configure call,
                    // before any tenant routing happens.
                    tenants.Register("alpha", Servers.SqlServerConnectionString);
                });

            opts.Services.AddDbContextWithWolverineManagedMultiTenancy<ItemsDbContext>(
                (builder, connectionString, _) =>
                {
                    builder.UseSqlServer(connectionString.Value);
                },
                AutoCreate.None);
        });

        using var host = builder.Build();
        await host.StartAsync();

        // Force WolverineOptions singleton resolution. The bug from #2739
        // fires inside the WolverineOptions factory lambda in
        // HostBuilderExtensions.AddWolverine — specifically the
        // `options.ApplyExtensions(extensions.ToArray())` line, which
        // calls EntityFrameworkCoreBackedPersistence<T>.Configure, which
        // (on V5.39.0) tries options.Services.AddSingleton<ISagaStoreDiagnostics>
        // against the already-frozen IServiceCollection.
        var options = host.Services.GetRequiredService<WolverineOptions>();
        options.ShouldNotBeNull();

        // Reaching here without an InvalidOperationException is the
        // assertion — the bug from #2739 fired during this resolution.
        host.ShouldNotBeNull();
    }
}
