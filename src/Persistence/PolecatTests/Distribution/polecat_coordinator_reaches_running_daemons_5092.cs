using IntegrationTests;
using JasperFx.Core;
using JasperFx.Events.Projections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Polecat;
using PolecatTests.Distribution.TripDomain;
using Shouldly;
using Wolverine;
using Wolverine.Polecat;
using Wolverine.Runtime.Agents;
using Wolverine.Tracking;

namespace PolecatTests.Distribution;

// GH-5092: under managed distribution a store-agnostic caller (CritterWatch) finds the Polecat coordinator
// through the JasperFx IProjectionCoordinator, and it answers with only the daemons this node is running.
// Wolverine.Polecat's coordinator interface did not extend the JasperFx one, so it could not be found that way.
public class polecat_coordinator_reaches_running_daemons_5092 : IAsyncLifetime
{
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.HealthCheckPollingTime = 1.Seconds();
                opts.Durability.CheckAssignmentPeriod = 1.Seconds();

                opts.Services.AddPolecat(m =>
                    {
                        m.ConnectionString = Servers.SqlServerConnectionString;
                        m.DatabaseSchemaName = "polecat_5092";

                        m.Projections.Add<TripProjection>(ProjectionLifecycle.Async);
                        m.Projections.Add<DayProjection>(ProjectionLifecycle.Async);
                    })
                    .IntegrateWithWolverine(o => o.UseWolverineManagedEventSubscriptionDistribution = true)
                    .ApplyAllDatabaseChangesOnStartup();

                opts.Discovery.DisableConventionalDiscovery();
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        _host.GetRuntime().Agents.DisableHealthChecks();
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task the_coordinator_is_found_through_the_jasperfx_interface_and_answers_with_the_local_daemon()
    {
        await _host.WaitUntilAssumesLeadershipAsync(5.Seconds());
        await _host.WaitUntilAssignmentsChangeTo(w =>
        {
            w.AgentScheme = EventSubscriptionAgentFamily.SchemeName;
            w.ExpectRunningAgents(_host, 2);
        }, 30.Seconds());

        // The walk CritterWatch does: every registered interface that extends the JasperFx coordinator
        var registrations = _host.GetRuntime().Options.Services;
        var coordinators = registrations
            .Select(x => x.ServiceType)
            .Where(type => type.IsInterface && !type.IsGenericTypeDefinition
                                            && typeof(JasperFx.Events.Daemon.IProjectionCoordinator).IsAssignableFrom(type))
            .Distinct()
            .SelectMany(type => _host.Services.GetServices(type))
            .OfType<JasperFx.Events.Daemon.IProjectionCoordinator>()
            .Distinct()
            .ToList();

        var coordinator = coordinators.ShouldHaveSingleItem();

        var daemon = (await coordinator.AllDaemonsAsync()).ShouldHaveSingleItem();
        daemon.IsRunning.ShouldBeTrue();

        coordinator.DaemonForMainDatabase().ShouldBeSameAs(daemon);
        await Should.ThrowAsync<DaemonNotRunningOnThisNodeException>(
            async () => await coordinator.DaemonForDatabase("not-a-database-here"));
    }
}
