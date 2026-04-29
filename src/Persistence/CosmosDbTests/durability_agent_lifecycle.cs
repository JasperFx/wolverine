using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.CosmosDb;
using Wolverine.CosmosDb.Internals.Durability;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;

namespace CosmosDbTests;

// Companion guard for the CosmosDb side of #2623. RavenDb had a duplicate-poller bug
// because RavenDbMessageStore.BuildAgentFamily registered a competing
// wolverinedb://ravendb/durability agent and StartScheduledJobs *also* started its own.
// CosmosDbMessageStore.BuildAgentFamily returns null, so only the one returned from
// StartScheduledJobs polls — but if anyone ever wires a CosmosDb agent family in the
// future, this test will fail loudly the first time two pollers appear.
[Collection("cosmosdb")]
[Trait("Category", "Flaky")]
public class durability_agent_lifecycle : IAsyncLifetime
{
    private readonly AppFixture _fixture;
    private IHost _host = null!;

    public durability_agent_lifecycle(AppFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        await _fixture.ClearAll();

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.UseCosmosDbPersistence(AppFixture.DatabaseName);
                opts.Services.AddSingleton(_fixture.Client);
                opts.ServiceName = "durability-agent-lifecycle";
            }).StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public void only_one_durability_agent_polls_after_host_start()
    {
        var live = liveDurabilityAgents(_host).Where(a => a.IsPolling).ToList();
        live.Count.ShouldBe(1);
    }

    private static IEnumerable<CosmosDbDurabilityAgent> liveDurabilityAgents(IHost host)
    {
        var runtime = (WolverineRuntime)host.Services.GetRequiredService<IWolverineRuntime>();

        // Agent built via IAgentFamily and registered with NodeAgentController — this is
        // the one we expect to be polling.
        if (runtime.NodeController != null)
        {
            foreach (var agent in runtime.NodeController.Agents.Values.OfType<CosmosDbDurabilityAgent>())
                yield return agent;
        }

        // Agent built by CosmosDbMessageStore.StartScheduledJobs and held in
        // WolverineRuntime.DurableScheduledJobs (CompositeAgent) purely for
        // disposal-time StopAsync — must NOT have started its timers after the fix.
        if (runtime.DurableScheduledJobs is CompositeAgent composite)
        {
            foreach (var agent in composite.InnerAgents.OfType<CosmosDbDurabilityAgent>())
                yield return agent;
        }
        else if (runtime.DurableScheduledJobs is CosmosDbDurabilityAgent single)
        {
            yield return single;
        }
    }
}
