using IntegrationTests;
using JasperFx.Core.Reflection;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Polecat;
using Polecat.Internal;
using PolecatTests.Distribution.TripDomain;
using Shouldly;
using Wolverine;
using Wolverine.Polecat;
using Wolverine.Polecat.Distribution;

namespace PolecatTests.Bugs;

// GH-3290 (Polecat parity with the Marten side): with
// UseWolverineManagedEventSubscriptionDistribution = true, Wolverine replaces Polecat's
// own daemon/coordinator hosting outright, but the store's only knowledge of the daemon
// state was DaemonSettings.AsyncMode, which the integration never set. The integration
// now records DaemonMode.ExternallyManaged (jasperfx#490) — the same runtime posture as
// Disabled (nothing Polecat-hosted starts), but any AsyncMode reader sees that the async
// projections DO run under Wolverine's distribution.
public class Bug_3290_externally_managed_daemon_mode
{
    private static IHostBuilder configureHost(bool useWolverineManagedDistribution,
        DaemonMode? explicitDaemonMode = null,
        bool explicitDaemonBeforeIntegration = false)
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                var polecat = opts.Services.AddPolecat(m =>
                {
                    m.ConnectionString = Servers.SqlServerConnectionString;
                    m.DatabaseSchemaName = "bug3290";

                    m.Projections.Add<TripProjection>(ProjectionLifecycle.Async);
                });

                if (explicitDaemonMode.HasValue && explicitDaemonBeforeIntegration)
                {
                    polecat.AddAsyncDaemon(explicitDaemonMode.Value);
                }

                polecat.IntegrateWithWolverine(o =>
                {
                    o.UseWolverineManagedEventSubscriptionDistribution = useWolverineManagedDistribution;
                });

                if (explicitDaemonMode.HasValue && !explicitDaemonBeforeIntegration)
                {
                    polecat.AddAsyncDaemon(explicitDaemonMode.Value);
                }
            });
    }

    private static DocumentStore buildStore(IHost host)
    {
        return host.Services.GetRequiredService<IDocumentStore>().As<DocumentStore>();
    }

    [Fact]
    public void managed_distribution_records_externally_managed()
    {
        using var host = configureHost(useWolverineManagedDistribution: true).Build();

        var store = buildStore(host);

        store.Options.DaemonSettings.AsyncMode.ShouldBe(DaemonMode.ExternallyManaged);
    }

    [Fact]
    public void managed_distribution_starts_nothing_polecat_hosted()
    {
        using var host = configureHost(useWolverineManagedDistribution: true).Build();

        buildStore(host);

        // The same runtime posture as Disabled: Polecat's daemon hosted service and its
        // coordinator only ever enter the container as hosted services via the user's
        // explicit AddAsyncDaemon()/AddProjectionCoordinator() calls, so no registered
        // hosted service may be a daemon runner or a projection coordinator of any flavor.
        var hostedServices = host.Services.GetServices<IHostedService>().ToArray();
        hostedServices.OfType<PolecatDaemonHostedService>().ShouldBeEmpty();
        hostedServices.OfType<JasperFx.Events.Daemon.IProjectionCoordinator>().ShouldBeEmpty();
        hostedServices.OfType<Wolverine.Polecat.Distribution.IProjectionCoordinator>().ShouldBeEmpty();

        // The daemon coordination surface is Wolverine's distribution-backed coordinator,
        // registered as a plain singleton — never hosted
        host.Services.GetRequiredService<Wolverine.Polecat.Distribution.IProjectionCoordinator>()
            .ShouldBeOfType<WolverineProjectionCoordinator>();
    }

    [Fact]
    public void without_managed_distribution_the_mode_stays_disabled()
    {
        using var host = configureHost(useWolverineManagedDistribution: false).Build();

        var store = buildStore(host);

        store.Options.DaemonSettings.AsyncMode.ShouldBe(DaemonMode.Disabled);
    }

    [Fact]
    public void explicit_add_async_daemon_choice_after_integration_is_not_overwritten()
    {
        using var host = configureHost(useWolverineManagedDistribution: true, DaemonMode.Solo).Build();

        var store = buildStore(host);

        store.Options.DaemonSettings.AsyncMode.ShouldBe(DaemonMode.Solo);
    }

    [Fact]
    public void explicit_add_async_daemon_choice_before_integration_is_not_overwritten()
    {
        using var host = configureHost(useWolverineManagedDistribution: true, DaemonMode.Solo,
            explicitDaemonBeforeIntegration: true).Build();

        var store = buildStore(host);

        store.Options.DaemonSettings.AsyncMode.ShouldBe(DaemonMode.Solo);
    }
}
