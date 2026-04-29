using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Raven.Client.Documents;
using Shouldly;
using Wolverine;
using Wolverine.RavenDb;
using Wolverine.RavenDb.Internals.Durability;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;

namespace RavenDbTests;

// Regression coverage for #2623 — RavenDbMessageStore.StartScheduledJobs used to
// eagerly call StartTimers() on a freshly built RavenDbDurabilityAgent in addition
// to the agent that NodeAgentController already builds and starts via
// IAgentFamily / MessageStoreCollection. Result: two RavenDbDurabilityAgent
// instances polled the same database and raced each other on the scheduled-job
// lock. The fix is to drop the eager StartTimers() call; this test pins down that
// behavior by asserting only one of the runtime's RavenDbDurabilityAgent instances
// has its polling timers wired up after host startup.
[Collection("raven")]
public class durability_agent_lifecycle : IAsyncLifetime
{
    private readonly DatabaseFixture _fixture;
    private IDocumentStore _store = null!;
    private IHost _host = null!;

    public durability_agent_lifecycle(DatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _store = _fixture.StartRavenStore();
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton(_store);
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.ServiceName = "durability-agent-lifecycle";
                opts.UseRavenDbPersistence();
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

    private static IEnumerable<RavenDbDurabilityAgent> liveDurabilityAgents(IHost host)
    {
        var runtime = (WolverineRuntime)host.Services.GetRequiredService<IWolverineRuntime>();

        // Agent built via IAgentFamily and registered with NodeAgentController under
        // wolverinedb://ravendb/durability — this is the one we expect to be polling.
        if (runtime.NodeController != null)
        {
            foreach (var agent in runtime.NodeController.Agents.Values.OfType<RavenDbDurabilityAgent>())
                yield return agent;
        }

        // Agent built by RavenDbMessageStore.StartScheduledJobs and held in
        // WolverineRuntime.DurableScheduledJobs (CompositeAgent) purely for
        // disposal-time StopAsync — must NOT have started its timers after the fix.
        if (runtime.DurableScheduledJobs is CompositeAgent composite)
        {
            foreach (var agent in composite.InnerAgents.OfType<RavenDbDurabilityAgent>())
                yield return agent;
        }
        else if (runtime.DurableScheduledJobs is RavenDbDurabilityAgent single)
        {
            yield return single;
        }
    }
}
