using JasperFx.Events;
using JasperFx.Events.Daemon;
using Wolverine.Configuration;
using Wolverine.ErrorHandling;
using Wolverine.Logging;
using Wolverine.Persistence;
using Wolverine.Runtime.Metrics;
using Wolverine.Runtime.Routing;
using Wolverine.Transports;

namespace Wolverine.Runtime.Agents;

public interface IWolverineObserver
{
    Task AssumedLeadership();

    /// <summary>
    /// The node was leader, detected that its underlying advisory lock had
    /// been released server-side, and has stepped down. Default no-op so
    /// existing custom <see cref="IWolverineObserver"/> implementations are
    /// unaffected. See GH-2602.
    /// </summary>
    Task LostLeadership() => Task.CompletedTask;

    Task NodeStarted();
    Task NodeStopped();
    Task AgentStarted(Uri agentUri);
    Task AgentStopped(Uri agentUri);

    /// <summary>
    /// A locally-owned agent stopped running because of a failure it reported, not because it was asked
    /// to stop. The case this exists for is an event-subscription shard the daemon paused on an
    /// <c>ApplyEventException</c> under a non-skipping error policy: the anti-thrash guards already keep
    /// it from being restarted in a loop, but nothing surfaced it, so the projection's progress simply
    /// flatlined and the only trace was a log line on one node.
    ///
    /// <para>Fires once per transition into the failed state — not on every health-check tick — and again
    /// if the agent recovers and fails anew. <paramref name="failure" /> is the classified reason
    /// (category, the failing event, the root exception type) when the agent can report one, and null
    /// when all that is known is that it stopped. Default no-op so existing observers are unaffected.
    /// See GH-3637 / GH-3638.</para>
    /// </summary>
    Task AgentPaused(Uri agentUri, ShardFailure? failure) => Task.CompletedTask;

    // Loop through and decide what you want here. 
    Task AssignmentsChanged(AssignmentGrid grid, AgentCommands commands);
    
    // TODO -- more for listener stopped/started
    Task StaleNodes(IReadOnlyList<WolverineNode> staleNodes);
    Task RuntimeIsFullyStarted();
    void EndpointAdded(Endpoint endpoint);
    void MessageRouted(Type messageType, IMessageRouter router);

    Task BackPressureTriggered(Endpoint endpoint, IListeningAgent agent);
    Task BackPressureLifted(Endpoint endpoint);
    Task ListenerLatched(Endpoint endpoint);
    Task CircuitBreakerTripped(Endpoint endpoint, CircuitBreakerOptions options);
    Task CircuitBreakerReset(Endpoint endpoint);
    void PersistedCounts(Uri storeUri, PersistedCounts counts);
    void MessageHandlingMetricsExported(MessageHandlingMetrics metrics);

    /// <summary>
    /// Called when a new message causation pair is discovered at runtime.
    /// Latched: only fires once per unique (incomingType, outgoingType) pair.
    /// </summary>
    void MessageCausedBy(string incomingMessageType, string outgoingMessageType, string handlerType, string? endpointUri)
    {
        // Default no-op implementation
    }

    /// <summary>
    /// Called when events are appended to an event store during a message/endpoint execution
    /// enrolled in a Wolverine outbox. The store-specific integration (Wolverine.Marten /
    /// Wolverine.Polecat) reads the pending/committed events and reports them here so a custom
    /// observer (e.g. CritterWatch) can attribute each appended event to the executing handler /
    /// endpoint — a relationship that <see cref="MessageCausedBy"/> can't see, because appended
    /// events go to the event store, never to the message outbox. Only fired when
    /// <c>WolverineOptions.Tracking.EnableEventAppendTracking</c> is enabled. Default no-op so
    /// existing observers are unaffected.
    /// </summary>
    void EventsAppended(IReadOnlyList<IEvent> events)
    {
        // Default no-op implementation
    }

    /// <summary>
    /// Called once per database *server* per metrics sweep with the connections currently open on
    /// it and the budget they're measured against. A sibling of <see cref="PersistedCounts"/> rather
    /// than a member of it, because connections are a server-scoped resource while the envelope
    /// counts are database-scoped — in a sharded deployment many databases report against one
    /// budget. Only fired when the budget machinery is active (see
    /// <c>DurabilitySettings.ConnectionBudgets</c>). Default no-op so existing observers are
    /// unaffected. See #3397.
    /// </summary>
    void ConnectionBudget(ConnectionBudgetSnapshot snapshot)
    {
        // Default no-op implementation
    }
}

internal class PersistenceWolverineObserver : IWolverineObserver
{
    // The narrowest wolverine_node_records.description column any store provisions.
    private const int DescriptionLength = 500;

    private static string truncate(string text, int max)
        => text.Length <= max ? text : text[..(max - 3)] + "...";

    private readonly IWolverineRuntime _runtime;

    public PersistenceWolverineObserver(IWolverineRuntime runtime)
    {
        _runtime = runtime;
    }

    public Task BackPressureTriggered(Endpoint endpoint, IListeningAgent agent)
    {
        return Task.CompletedTask;
    }

    public Task BackPressureLifted(Endpoint endpoint)
    {
        return Task.CompletedTask;
    }

    public async Task ListenerLatched(Endpoint endpoint)
    {
        try
        {
            await _runtime.Storage.Nodes.LogRecordsAsync(NodeRecord.For(_runtime.Options,
                NodeRecordType.ListenerLatched, endpoint.Uri));
        }
        catch (NotSupportedException)
        {
            // NullMessageStore does not support node persistence
        }
    }

    public void PersistedCounts(Uri storeUri, PersistedCounts counts)
    {
        // Nothing
    }

    public void MessageHandlingMetricsExported(MessageHandlingMetrics metrics)
    {
        // Nothing
    }

    public async Task AssumedLeadership()
    {
        await _runtime.Storage.Nodes.LogRecordsAsync(NodeRecord.For(_runtime.Options,
            NodeRecordType.LeadershipAssumed, NodeAgentController.LeaderUri));
    }

    public async Task LostLeadership()
    {
        try
        {
            await _runtime.Storage.Nodes.LogRecordsAsync(NodeRecord.For(_runtime.Options,
                NodeRecordType.LeadershipLost, NodeAgentController.LeaderUri));
        }
        catch (NotSupportedException)
        {
            // NullMessageStore does not support node persistence; nothing to log.
        }
    }

    public async Task NodeStarted()
    {
        try
        {
            await _runtime.Storage.Nodes.LogRecordsAsync(NodeRecord.For(_runtime.Options, NodeRecordType.NodeStarted));
        }
        catch (NotSupportedException)
        {
            // NullMessageStore does not support node persistence; a storeless Solo node still
            // fires this bookend (via SoloHeartbeatService) but has nowhere to log it. See #3188.
        }
    }

    public async Task NodeStopped()
    {
        try
        {
            await _runtime.Storage.Nodes.LogRecordsAsync(NodeRecord.For(_runtime.Options, NodeRecordType.NodeStopped));
        }
        catch (NotSupportedException)
        {
            // NullMessageStore does not support node persistence; nothing to log.
        }
    }

    public async Task AgentStarted(Uri agentUri)
    {
        await _runtime.Storage.Nodes.LogRecordsAsync(NodeRecord.For(_runtime.Options, NodeRecordType.AgentStarted,
            agentUri));
    }

    public async Task AgentStopped(Uri agentUri)
    {
        await _runtime.Storage.Nodes.LogRecordsAsync(NodeRecord.For(_runtime.Options, NodeRecordType.AgentStopped,
            agentUri));
    }

    public async Task AgentPaused(Uri agentUri, ShardFailure? failure)
    {
        var record = NodeRecord.For(_runtime.Options, NodeRecordType.AgentPaused, agentUri);

        // NodeRecord.For seeds Description with the agent URI, which the record already carries in
        // AgentUri; the reason is the whole point of this record, so it takes the slot when we have one.
        // Deliberately ShardFailure.ToString() (category + failing event + message) rather than Detail:
        // the full exception text with stack traces belongs in the log line the caller writes, not in a
        // node-record column every store has to hold. Truncated to the narrowest description column any
        // store provisions (varchar(500) on SQL Server and Oracle) -- an exception message long enough to
        // overflow it must not turn this diagnostic record into a failed insert.
        if (failure != null)
        {
            record.Description = truncate(failure.ToString(), DescriptionLength);
        }

        try
        {
            await _runtime.Storage.Nodes.LogRecordsAsync(record);
        }
        catch (NotSupportedException)
        {
            // NullMessageStore does not support node persistence; a storeless Solo node can still run
            // event-subscription agents, and losing the record must not break the health-check sweep.
        }
    }

    public async Task StaleNodes(IReadOnlyList<WolverineNode> staleNodes)
    {
        var records = staleNodes.Select(x => new NodeRecord
        {
            NodeNumber = x.AssignedNodeNumber,
            RecordType = NodeRecordType.DormantNodeEjected,
            Description = "Health check on Node " + _runtime.Options.Durability.AssignedNodeNumber
        }).ToArray();

        await _runtime.Storage.Nodes.LogRecordsAsync(records);
    }

    public Task RuntimeIsFullyStarted()
    {
        return Task.CompletedTask;
    }

    public void EndpointAdded(Endpoint endpoint)
    {
        // Nothing, this is a hook for CritterWatch
    }

    public void MessageRouted(Type messageType, IMessageRouter router)
    {
        // Nothing, this is a hook for CritterWatch
    }

    public async Task AssignmentsChanged(AssignmentGrid grid, AgentCommands commands)
    {
        if (!commands.Any()) return;

        var records = commands.Select(x => new NodeRecord
        {
            NodeNumber = _runtime.Options.Durability.AssignedNodeNumber,
            RecordType = NodeRecordType.AssignmentChanged,
            Description = x.ToString()!
        }).ToArray();

        await _runtime.Storage.Nodes.LogRecordsAsync(records);
    }

    public Task CircuitBreakerTripped(Endpoint endpoint, CircuitBreakerOptions options)
    {
        return Task.CompletedTask;
    }

    public Task CircuitBreakerReset(Endpoint endpoint)
    {
        return Task.CompletedTask;
    }
}