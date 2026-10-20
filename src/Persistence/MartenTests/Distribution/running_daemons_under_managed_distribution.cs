using JasperFx.Core;
using JasperFx.Descriptors;
using JasperFx.Events.Projections;
using Marten;
using MartenTests.Distribution.Support;
using MartenTests.Distribution.TripDomain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Runtime.Agents;
using Xunit;

namespace MartenTests.Distribution;

/// <summary>
/// GH-5092: under Wolverine-managed distribution, a node's projection coordinator answers with the
/// daemons THIS node is running and nothing else. CritterWatch reads every node's high water loop
/// through <c>IProjectionCoordinator.AllDaemonsAsync()</c> and restarts a stale one through
/// <c>DaemonForDatabase</c>; the first used to build and subscribe a daemon for every database, and the
/// second threw.
/// </summary>
public class running_daemons_under_managed_distribution(ITestOutputHelper output)
    : MultiTenantContext(output)
{
    [Fact]
    public async Task each_node_answers_with_only_the_daemons_for_the_databases_it_runs()
    {
        string[] tenants = ["tenant1", "tenant2", "tenant3"];
        await AddTenantsAsync(tenants);

        var extraHosts = await Task.WhenAll<IHost>([startHostAsync(), startHostAsync()]);
        IHost[] hosts = [theOriginalHost, .. extraHosts];

        // Database-affine assignment keeps a database's three agents together, but how the databases land
        // across the nodes is the leader's business, so wait for all nine to be running on more than one
        // node and then hold each node to whatever it was given
        await waitForAsync(() =>
        {
            var running = hosts.Select(databasesRunOn).ToArray();
            return running.Sum(x => x.Length) == tenants.Length && running.Count(x => x.Length > 0) > 1;
        }, 60.Seconds());

        foreach (var host in hosts)
        {
            var databases = databasesRunOn(host);
            var coordinator = host.Services
                .GetRequiredService<global::Marten.Events.Daemon.Coordination.IProjectionCoordinator>();

            // Read twice: a read must not leave anything behind that a second read would then see
            await coordinator.AllDaemonsAsync();
            var daemons = await coordinator.AllDaemonsAsync();

            daemons.Count.ShouldBe(databases.Length);

            foreach (var database in databases)
            {
                var daemon = await coordinator.DaemonForDatabase(database);
                daemons.ShouldContain(daemon);
                daemon.IsRunning.ShouldBeTrue("the node runs agents for the database, so its high water loop runs");
            }

            foreach (var other in tenants.Except(databases))
            {
                await Should.ThrowAsync<DaemonNotRunningOnThisNodeException>(
                    async () => await coordinator.DaemonForDatabase(other));
            }

            // The store-agnostic route CritterWatch takes answers the same way
            var jasperFxCoordinator = (JasperFx.Events.Daemon.IProjectionCoordinator)coordinator;
            (await jasperFxCoordinator.AllDaemonsAsync()).ShouldBe(daemons, ignoreOrder: true);
        }
    }

    [Fact]
    public async Task a_node_that_runs_nothing_answers_with_nothing()
    {
        await AddTenantsAsync("tenant1");

        await theOriginalHost.WaitUntilAssignmentsChangeTo(w =>
        {
            w.AgentScheme = EventSubscriptionAgentFamily.SchemeName;
            w.ExpectRunningAgents(theOriginalHost, 3);
        }, 60.Seconds());

        // One database, so all of its agents stay together on the first node
        var idle = await startHostAsync();
        await theOriginalHost.WaitUntilAssignmentsChangeTo(w =>
        {
            w.AgentScheme = EventSubscriptionAgentFamily.SchemeName;
            w.ExpectRunningAgents(theOriginalHost, 3);
            w.ExpectRunningAgents(idle, 0);
        }, 60.Seconds());

        var coordinator = idle.Services
            .GetRequiredService<global::Marten.Events.Daemon.Coordination.IProjectionCoordinator>();

        (await coordinator.AllDaemonsAsync()).ShouldBeEmpty();
        await Should.ThrowAsync<DaemonNotRunningOnThisNodeException>(
            async () => await coordinator.DaemonForDatabase("tenant1"));
    }

    private static string[] databasesRunOn(IHost host)
    {
        return host.RunningAgents()
            .Where(x => x.Scheme == EventSubscriptionAgentFamily.SchemeName)
            .Select(x => DatabaseId.Parse(x.Segments[2].TrimEnd('/')).Name)
            .Distinct()
            .ToArray();
    }

    private static async Task waitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        while (!condition())
        {
            if (cts.IsCancellationRequested)
            {
                throw new TimeoutException("The agents never settled across the nodes");
            }

            await Task.Delay(250);
        }
    }

    protected override void SetupProjections(StoreOptions storeOptions)
    {
        storeOptions.Projections.Add<TripProjection>(ProjectionLifecycle.Async);
        storeOptions.Projections.Add<DayProjection>(ProjectionLifecycle.Async);
        storeOptions.Projections.Add<DistanceProjection>(ProjectionLifecycle.Async);
    }
}
