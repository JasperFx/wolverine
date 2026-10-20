using Fisher;
using JasperFx;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Fisher;
using Wolverine.Runtime.Agents;
using Wolverine.Tracking;
using FisherCoordinator = Wolverine.Fisher.Distribution.IProjectionCoordinator;

namespace FisherTests;

/// <summary>
///     GH-5094. With <see cref="FisherIntegration.UseWolverineManagedEventSubscriptionDistribution" />, a Fisher
///     host runs its async projections as Wolverine event subscription agents, so operator tooling (CritterWatch
///     #1437) pauses, restarts and rewinds them through the agent family the same way it does on Marten and Polecat.
/// </summary>
public class fisher_managed_event_subscription_distribution_5094 : IAsyncLifetime
{
    private FisherTestDatabase theDatabase = null!;
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        theDatabase = Servers.CreateDatabase("managed_distribution_5094");

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery();
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Services.AddFisher(m =>
                    {
                        m.Connection(theDatabase.ConnectionString);
                        m.AutoCreateSchemaObjects = AutoCreate.All;
                        m.Projections.Snapshot<ManagedTally>(SnapshotLifecycle.Async);
                    })
                    .ApplyAllDatabaseChangesOnStartup()
                    .IntegrateWithWolverine(x => x.UseWolverineManagedEventSubscriptionDistribution = true);
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
        theDatabase.Dispose();
    }

    [Fact]
    public void registers_the_event_subscription_agent_family()
    {
        _host.Services.GetServices<IEventSubscriptionAgentFamily>().ShouldHaveSingleItem();
        _host.Services.GetServices<IAgentFamily>().OfType<EventSubscriptionAgentFamily>().ShouldHaveSingleItem();

        // Wolverine runs the daemon, not Fisher's own hosted service
        _host.Services.GetServices<IProjectionCoordinator>().ShouldBeEmpty();
        _host.Services.GetRequiredService<FisherCoordinator>().GetType().Name.ShouldBe("WolverineProjectionCoordinator");
    }

    [Fact]
    public async Task the_projection_is_an_agent_that_tooling_can_find()
    {
        var family = _host.Services.GetRequiredService<IEventSubscriptionAgentFamily>();

        var uri = await family.FindAgentUriAsync("ManagedTally:All", null, TestContext.Current.CancellationToken);

        uri.ShouldNotBeNull();
        uri.Scheme.ShouldBe(EventSubscriptionAgentFamily.SchemeName);
        uri.AbsolutePath.TrimEnd('/').ShouldEndWith("/managedtally/all");
    }

    [Fact]
    public async Task the_agent_runs_on_this_node_and_projects()
    {
        var family = _host.Services.GetRequiredService<IEventSubscriptionAgentFamily>();
        var uri = await family.FindAgentUriAsync("ManagedTally:All", null, TestContext.Current.CancellationToken);

        var runtime = _host.GetRuntime();
        await waitFor(() => runtime.Agents.AllRunningAgentUris().Contains(uri!), "the projection agent to start");

        var id = Guid.NewGuid();
        var store = _host.Services.GetRequiredService<IDocumentStore>();
        await using (var session = store.LightweightSession())
        {
            session.Events.StartStream<ManagedTally>(id, new ManagedTallied(2), new ManagedTallied(3));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        ManagedTally? tally = null;
        await waitFor(async () =>
        {
            await using var query = store.QuerySession();
            tally = await query.LoadAsync<ManagedTally>(id, TestContext.Current.CancellationToken);
            return tally is { Total: 5 };
        }, "the async projection to catch up");
    }

    [Fact]
    public async Task pausing_through_the_coordinator_stops_the_agent()
    {
        var family = _host.Services.GetRequiredService<IEventSubscriptionAgentFamily>();
        var uri = await family.FindAgentUriAsync("ManagedTally:All", null, TestContext.Current.CancellationToken);
        var runtime = _host.GetRuntime();
        await waitFor(() => runtime.Agents.AllRunningAgentUris().Contains(uri!), "the projection agent to start");

        var coordinator = _host.Services.GetRequiredService<FisherCoordinator>();
        var daemon = (await coordinator.AllDaemonsAsync()).ShouldHaveSingleItem();

        await coordinator.PauseAsync();
        await waitFor(() => daemon.CurrentAgents().Count == 0, "the daemon to stop its shards");

        await coordinator.ResumeAsync();
        await waitFor(() => daemon.CurrentAgents().Count > 0, "the daemon to restart its shards");
    }

    [Fact]
    public async Task refuses_fishers_own_solo_daemon_alongside_it()
    {
        using var database = Servers.CreateDatabase("managed_distribution_5094_solo");

        var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            using var host = await Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.Discovery.DisableConventionalDiscovery();
                    opts.Durability.Mode = DurabilityMode.Solo;

                    opts.Services.AddFisher(m =>
                        {
                            m.Connection(database.ConnectionString);
                            m.AutoCreateSchemaObjects = AutoCreate.All;
                            m.Projections.Snapshot<ManagedTally>(SnapshotLifecycle.Async);
                        })
                        .AddAsyncDaemon(DaemonMode.Solo)
                        .IntegrateWithWolverine(x => x.UseWolverineManagedEventSubscriptionDistribution = true);
                }).StartAsync();
        });

        ex.Message.ShouldContain("UseWolverineManagedEventSubscriptionDistribution");
    }

    private static Task waitFor(Func<bool> condition, string what) => waitFor(() => Task.FromResult(condition()), what);

    private static async Task waitFor(Func<Task<bool>> condition, string what)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(100);
        }

        throw new TimeoutException($"Timed out waiting for {what}");
    }
}

public record ManagedTallied(int Amount);

public class ManagedTally
{
    public Guid Id { get; set; }
    public int Total { get; set; }

    public void Apply(ManagedTallied e) => Total += e.Amount;
}
