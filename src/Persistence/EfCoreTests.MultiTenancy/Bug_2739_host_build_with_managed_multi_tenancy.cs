using IntegrationTests;
using JasperFx;
using Microsoft.EntityFrameworkCore;
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
/// On Wolverine **5.39.0** (the NuGet release tagged from V5.39.0 = commit
/// 82ee0bb0a on the now-6.0-development trajectory), an app that called
/// <c>AddDbContextWithWolverineManagedMultiTenancy&lt;T&gt;(...)</c> threw on
/// <c>app.Run()</c>:
///
/// <code>
///   System.InvalidOperationException:
///     As of Wolverine 3.0, it's no longer supported to alter IoC service
///     registrations through Wolverine extensions that are themselves
///     registered in the IoC container
///   at Wolverine.WolverineOptions.ApplyExtensions(IWolverineExtension[] extensions)
///   at Wolverine.HostBuilderExtensions.&lt;&gt;c__DisplayClass3_0.&lt;AddWolverine&gt;b__2(IServiceProvider s)
/// </code>
///
/// The bug was introduced by commit <c>28b06d600</c> (the
/// <c>ISagaStoreDiagnostics</c> work for #2713), which made
/// <c>EntityFrameworkCoreBackedPersistence&lt;T&gt;.Configure</c> call
/// <c>options.Services.AddSingleton&lt;ISagaStoreDiagnostics&gt;(...)</c> at
/// host-build time — past the point where <c>IServiceCollection</c> is
/// still mutable. That commit is on the <c>main</c> trajectory (where it
/// was subsequently fixed in PR #2738), <b>not</b> on this <c>5.0</c>
/// maintenance branch.
///
/// This test therefore <b>passes on 5.0</b> as written — the buggy code
/// path doesn't exist here. Its purpose is to lock in the regression-guard
/// so the bug never re-enters this branch, and to provide a cherry-pickable
/// commit when the test gets backported to <c>main</c> (where the fix
/// from #2738 already keeps it green).
///
/// The test deliberately uses <c>AutoCreate.None</c> and never opens a
/// tenant database — the bug fires during the host's <i>first singleton
/// resolution</i> (StartAsync's <c>IWolverineRuntime</c> activation), well
/// before any database I/O. The single master <c>PersistMessagesWithSqlServer</c>
/// call needs a reachable SQL Server because the master message-store schema
/// gets provisioned, but the test runs without configuring per-tenant
/// databases.
/// </summary>
[Collection("multi-tenancy")]
public class Bug_2739_host_build_with_managed_multi_tenancy
{
    [Fact]
    public async Task host_build_does_not_throw_with_AddDbContextWithWolverineManagedMultiTenancy()
    {
        // Mirrors the user's reported scenario: master message-store + one
        // call to AddDbContextWithWolverineManagedMultiTenancy. AutoCreate.None
        // means we don't provision per-tenant schema during host build, so
        // the test exercises the configuration / DI surface only.
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
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
            })
            .StartAsync();

        // Reaching here without an InvalidOperationException is the
        // assertion — the bug from #2739 fired during StartAsync.
        host.ShouldNotBeNull();
    }
}
