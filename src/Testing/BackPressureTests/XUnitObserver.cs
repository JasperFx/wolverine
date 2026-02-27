using Wolverine.Configuration;
using Wolverine.Logging;
using Wolverine.Runtime.Agents;
using Wolverine.Runtime.Metrics;
using Wolverine.Runtime.Routing;
using Wolverine.Transports;
using Xunit.Abstractions;

namespace BackPressureTests;

public class XUnitObserver(ITestOutputHelper Output) : IWolverineObserver
{
    public TaskCompletionSource Triggered { get; set; } = new();
    public TaskCompletionSource Lifted { get; set; } = new();
    
    public void Reset()
    {
        Triggered = new();
        Lifted = new();
    }
    
    public Task AssumedLeadership()
    {
        return Task.CompletedTask;
    }

    public Task NodeStarted()
    {
        return Task.CompletedTask;
    }

    public Task NodeStopped()
    {
        return Task.CompletedTask;
    }

    public Task AgentStarted(Uri agentUri)
    {
        return Task.CompletedTask;
    }

    public Task AgentStopped(Uri agentUri)
    {
        return Task.CompletedTask;
    }

    public Task AssignmentsChanged(AssignmentGrid grid, AgentCommands commands)
    {
        return Task.CompletedTask;
    }

    public Task StaleNodes(IReadOnlyList<WolverineNode> staleNodes)
    {
        return Task.CompletedTask;
    }

    public Task RuntimeIsFullyStarted()
    {
        Output.WriteLine("The WolverineRuntime is fully started");
        return Task.CompletedTask;
    }

    public void EndpointAdded(Endpoint endpoint)
    {

    }

    public void MessageRouted(Type messageType, IMessageRouter router)
    {
    }

    public Task BackPressureTriggered(Endpoint endpoint, IListeningAgent agent)
    {
        Output.WriteLine("Back Pressure was Triggerd!");
        Triggered?.TrySetResult();
        return Task.CompletedTask;

    }

    public Task BackPressureLifted(Endpoint endpoint)
    {
        Output.WriteLine("Back Pressure was Lifted!");
        Lifted?.TrySetResult();
        return Task.CompletedTask;
    }

    public Task ListenerLatched(Endpoint endpoint)
    {
        Output.WriteLine($"Listener at {endpoint.Uri} has been permanently latched");
        return Task.CompletedTask;
    }

    public void PersistedCounts(Uri storeUri, PersistedCounts counts)
    {
        // Nothing here...
    }

    public void MessageHandlingMetricsExported(MessageHandlingMetrics metrics)
    {
        // Nothing here...
    }
}