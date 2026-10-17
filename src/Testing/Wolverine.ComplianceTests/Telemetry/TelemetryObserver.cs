using JasperFx.Core;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using Wolverine.Configuration;
using Wolverine.ErrorHandling;
using Wolverine.Logging;
using Wolverine.Persistence;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Wolverine.Runtime.Metrics;
using Wolverine.Runtime.Routing;
using Wolverine.Transports;

namespace Wolverine.ComplianceTests.Telemetry;

/// <summary>
///     Hears what one node's <see cref="IWolverineObserver" /> hears, records the leadership and agent events into
///     <see cref="AgentAssignmentTelemetry" />, and forwards every call to the observer it replaced -- including the
///     members the interface gives a default body, which a decorator that left them out would silently swallow.
/// </summary>
internal sealed class TelemetryObserver : IWolverineObserver
{
    private readonly AgentAssignmentTelemetry _telemetry;
    private readonly IWolverineRuntime _runtime;
    private readonly IWolverineObserver _inner;

    public TelemetryObserver(AgentAssignmentTelemetry telemetry, IWolverineRuntime runtime, IWolverineObserver inner)
    {
        _telemetry = telemetry;
        _runtime = runtime;
        _inner = inner;
    }

    // Read at the moment of the event: a node adopts its assigned number before it reports NodeStarted (GH-3698)
    private int node => _runtime.Options.Durability.AssignedNodeNumber;

    private void recordSafely(Func<ObservedEvent> describe)
    {
        // The telemetry is evidence, never a cause: a fault in describing an event must not reach the node
        // controller that reported it
        try
        {
            _telemetry.Record(describe());
        }
        catch (Exception)
        {
            // ignored
        }
    }

    public Task AssumedLeadership()
    {
        recordSafely(() => new NodeEvent(node, "assumed leadership"));
        return _inner.AssumedLeadership();
    }

    public Task LostLeadership()
    {
        recordSafely(() => new NodeEvent(node, "lost leadership"));
        return _inner.LostLeadership();
    }

    public Task NodeStarted()
    {
        recordSafely(() => new NodeEvent(node, $"started as {_runtime.Options.UniqueNodeId}"));
        return _inner.NodeStarted();
    }

    public Task NodeStopped()
    {
        recordSafely(() => new NodeEvent(node, "stopped"));
        return _inner.NodeStopped();
    }

    public Task AgentStarted(Uri agentUri)
    {
        recordSafely(() => new AgentEvent(node, agentUri, "started"));
        return _inner.AgentStarted(agentUri);
    }

    public Task AgentStopped(Uri agentUri)
    {
        recordSafely(() => new AgentEvent(node, agentUri, "stopped"));
        return _inner.AgentStopped(agentUri);
    }

    public Task AgentPaused(Uri agentUri, ShardFailure? failure)
    {
        recordSafely(() => new AgentEvent(node, agentUri, failure is null ? "paused" : $"paused: {failure}"));
        return _inner.AgentPaused(agentUri, failure);
    }

    public Task AgentReleased(Uri agentUri, ShardFailure? failure)
    {
        recordSafely(() => new AgentEvent(node, agentUri, failure is null ? "released" : $"released: {failure}"));
        return _inner.AgentReleased(agentUri, failure);
    }

    public Task AgentStartFailed(Uri agentUri, ShardStartFailureReason reason, Exception exception)
    {
        recordSafely(() => new AgentEvent(node, agentUri, $"failed to start ({reason}): {exception.Message}"));
        return _inner.AgentStartFailed(agentUri, reason, exception);
    }

    public Task AssignmentsChanged(AssignmentGrid grid, AgentCommands commands)
    {
        recordSafely(() => Evaluation.From(node, grid, commands));
        return _inner.AssignmentsChanged(grid, commands);
    }

    public Task StaleNodes(IReadOnlyList<WolverineNode> staleNodes)
    {
        recordSafely(() => new NodeEvent(node,
            $"ejected stale node(s) {staleNodes.Select(x => x.AssignedNodeNumber.ToString()).Join(", ")}"));
        return _inner.StaleNodes(staleNodes);
    }

    public Task RuntimeIsFullyStarted() => _inner.RuntimeIsFullyStarted();

    public void EndpointAdded(Endpoint endpoint) => _inner.EndpointAdded(endpoint);

    public void MessageRouted(Type messageType, IMessageRouter router) => _inner.MessageRouted(messageType, router);

    public Task BackPressureTriggered(Endpoint endpoint, IListeningAgent agent) =>
        _inner.BackPressureTriggered(endpoint, agent);

    public Task BackPressureLifted(Endpoint endpoint) => _inner.BackPressureLifted(endpoint);

    public Task ListenerLatched(Endpoint endpoint) => _inner.ListenerLatched(endpoint);

    public Task CircuitBreakerTripped(Endpoint endpoint, CircuitBreakerOptions options) =>
        _inner.CircuitBreakerTripped(endpoint, options);

    public Task CircuitBreakerReset(Endpoint endpoint) => _inner.CircuitBreakerReset(endpoint);

    public void PersistedCounts(Uri storeUri, PersistedCounts counts) => _inner.PersistedCounts(storeUri, counts);

    public void MessageHandlingMetricsExported(MessageHandlingMetrics metrics) =>
        _inner.MessageHandlingMetricsExported(metrics);

    public void MessageCausedBy(string incomingMessageType, string outgoingMessageType, string handlerType,
        string? endpointUri) =>
        _inner.MessageCausedBy(incomingMessageType, outgoingMessageType, handlerType, endpointUri);

    public void EventsAppended(IReadOnlyList<IEvent> events) => _inner.EventsAppended(events);

    public void ConnectionBudget(ConnectionBudgetSnapshot snapshot) => _inner.ConnectionBudget(snapshot);
}
