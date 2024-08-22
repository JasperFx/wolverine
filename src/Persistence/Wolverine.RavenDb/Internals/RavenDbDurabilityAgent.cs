using Raven.Client.Documents;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;

namespace Wolverine.RavenDb.Internals;

public class RavenDbDurabilityAgent : IAgent
{
    private readonly IDocumentStore _store;
    private readonly IWolverineRuntime _runtime;

    public RavenDbDurabilityAgent(IDocumentStore store, IWolverineRuntime runtime)
    {
        _store = store;
        _runtime = runtime;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Uri Uri { get; set; }
    public AgentStatus Status { get; set; }
}