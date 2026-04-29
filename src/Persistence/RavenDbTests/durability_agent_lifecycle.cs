using System.Reflection;
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
        var live = LiveDurabilityAgents(_host).Where(HasStartedTimers).ToList();
        live.Count.ShouldBe(1);
    }

    private static readonly FieldInfo RecoveryTaskField = typeof(RavenDbDurabilityAgent).GetField(
        "_recoveryTask", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static readonly FieldInfo ScheduledJobField = typeof(RavenDbDurabilityAgent).GetField(
        "_scheduledJob", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static bool HasStartedTimers(RavenDbDurabilityAgent agent)
        => RecoveryTaskField.GetValue(agent) is not null || ScheduledJobField.GetValue(agent) is not null;

    private static IEnumerable<RavenDbDurabilityAgent> LiveDurabilityAgents(IHost host)
    {
        var runtime = (WolverineRuntime)host.Services.GetRequiredService<IWolverineRuntime>();

        if (runtime.NodeController != null)
        {
            foreach (var agent in runtime.NodeController.Agents.Values.OfType<RavenDbDurabilityAgent>())
                yield return agent;
        }

        // DurableScheduledJobs is internal; reach for it via reflection rather than
        // adding RavenDbTests to Wolverine's InternalsVisibleTo list.
        var prop = typeof(WolverineRuntime).GetProperty(
            "DurableScheduledJobs",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (prop?.GetValue(runtime) is IAgent root)
        {
            foreach (var agent in EnumerateInner(root).OfType<RavenDbDurabilityAgent>())
                yield return agent;
        }
    }

    private static IEnumerable<IAgent> EnumerateInner(IAgent agent)
    {
        if (agent is CompositeAgent composite)
        {
            var field = typeof(CompositeAgent).GetField(
                "_agents",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (field?.GetValue(composite) is IEnumerable<IAgent> inner)
            {
                foreach (var a in inner) yield return a;
            }
        }
        else
        {
            yield return agent;
        }
    }
}
