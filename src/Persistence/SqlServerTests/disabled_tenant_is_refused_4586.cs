using IntegrationTests;
using JasperFx.MultiTenancy;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;
using Wolverine.RDBMS.MultiTenancy;
using Wolverine.SqlServer;

namespace SqlServerTests;

/// <summary>
/// GH-4586. Two things were wrong with a disabled tenant here: the master tenant source resolved it
/// anyway on any lookup that missed its cache -- which is every lookup after DisableTenantAsync, since
/// that drops the cached entry -- and when it did refuse a tenant it reported "unknown" for a tenant
/// that is registered and simply switched off.
/// </summary>
public class disabled_tenant_is_refused_4586 : IAsyncLifetime
{
    private IHost _host = null!;
    private MasterTenantSource theSource = null!;
    private readonly string theTenant = "disabled-" + Guid.NewGuid().ToString("N")[..8];

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, "mt_disabled_4586")
                    .UseMasterTableTenancy(_ => { });
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync(TestContext.Current.CancellationToken);

        theSource = _host.Services.GetServices<IDynamicTenantSource<string>>().OfType<MasterTenantSource>().Single();

        await theSource.AddTenantAsync(theTenant, Servers.SqlServerConnectionString);
    }

    public async ValueTask DisposeAsync()
    {
        await theSource.RemoveTenantAsync(theTenant);
        await _host.StopAsync();
        _host.Dispose();
    }

    /// <summary>
    /// A second source over the same tenants table, standing in for every other node in the cluster --
    /// and for this node after a restart. It has never resolved this tenant, so it takes the cold path.
    /// </summary>
    private MasterTenantSource freshSource()
    {
        var store = _host.Services.GetRequiredService<IMessageStore>();
        var registry = (ITenantDatabaseRegistry)((MultiTenantedMessageStore)store).Main;

        return new MasterTenantSource(registry, _host.Services.GetRequiredService<WolverineOptions>());
    }

    [Fact]
    public async Task an_enabled_tenant_resolves()
    {
        (await theSource.FindAsync(theTenant)).ShouldNotBeNull();
        (await freshSource().FindAsync(theTenant)).ShouldNotBeNull();
    }

    [Fact]
    public async Task a_disabled_tenant_is_refused_as_disabled()
    {
        await theSource.DisableTenantAsync(theTenant);

        var ex = await Should.ThrowAsync<DisabledTenantException>(async () => await theSource.FindAsync(theTenant));
        ex.ShouldBeAssignableTo<UnknownTenantIdException>();

        await theSource.EnableTenantAsync(theTenant);
    }

    [Fact]
    public async Task a_disabled_tenant_is_refused_by_a_source_that_never_cached_it()
    {
        await theSource.DisableTenantAsync(theTenant);

        // the registry hands back the row whatever the disabled flag says, so this is the lookup that
        // used to succeed and hand out a connection string for a tenant the operator had switched off
        await Should.ThrowAsync<DisabledTenantException>(async () => await freshSource().FindAsync(theTenant));

        await theSource.EnableTenantAsync(theTenant);
    }

    [Fact]
    public async Task re_enabling_makes_the_tenant_resolvable_again()
    {
        await theSource.DisableTenantAsync(theTenant);
        await theSource.EnableTenantAsync(theTenant);

        (await theSource.FindAsync(theTenant)).ShouldNotBeNull();
        (await freshSource().FindAsync(theTenant)).ShouldNotBeNull();
    }

    [Fact]
    public async Task a_tenant_with_no_row_at_all_is_still_unknown_rather_than_disabled()
    {
        var ex = await Should.ThrowAsync<UnknownTenantIdException>(async () =>
            await freshSource().FindAsync("never-registered-" + Guid.NewGuid().ToString("N")[..8]));

        ex.ShouldNotBeOfType<DisabledTenantException>();
    }
}
