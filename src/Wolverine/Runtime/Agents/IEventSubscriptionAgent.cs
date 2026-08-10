using JasperFx.Events.Daemon;

namespace Wolverine.Runtime.Agents;

/// <summary>
/// Interface for agents that manage event store projections and subscriptions.
/// Implemented by both Wolverine.Marten and Wolverine.Polecat EventSubscriptionAgent
/// classes. CritterWatch depends on this interface to issue projection commands
/// without coupling to a specific event store provider.
/// </summary>
public interface IEventSubscriptionAgent : IAgent
{
    /// <summary>
    /// Rebuilds the projection from scratch. All projected data will be deleted
    /// and rebuilt from the event stream. This may take significant time for
    /// large event stores.
    /// </summary>
    Task RebuildAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Rewinds this subscription/projection to a point and lets it run forward from there. Because
    /// the agent is per-shard (and per-tenant under partitioning), this rewinds exactly this shard's
    /// progression. Under Wolverine-managed event-subscription distribution this is the ONLY rewind
    /// path — the Marten/Polecat <c>IProjectionCoordinator.DaemonForDatabase()</c> throws. Default
    /// throws so existing <see cref="IEventSubscriptionAgent" /> implementations are unaffected.
    /// </summary>
    /// <param name="sequenceFloor">Sequence to rewind to (0 = beginning). Ignored when <paramref name="timestamp"/> is supplied.</param>
    /// <param name="timestamp">Optional point-in-time to rewind to; replays events on/after this time.</param>
    Task RewindAsync(long? sequenceFloor, DateTimeOffset? timestamp, CancellationToken cancellationToken)
        => throw new NotSupportedException("This event-subscription agent does not support rewind.");

    /// <summary>
    /// WHY this shard was paused or stopped, when it was. <see cref="IAgent.Status" /> alone says a shard
    /// is <see cref="JasperFx.AgentStatus.Paused" /> but never what an operator should do about it, so
    /// progress could flatline with nothing actionable to alert on. The category distinguishes a poison
    /// event (needs a code fix or a skip) from a serialization fault, an unregistered event type, two
    /// processes racing the same shard, or a transient infrastructure blip — and the failing event's
    /// sequence names exactly where it stopped.
    ///
    /// <para>A plain serializable value rather than an <see cref="Exception" />, so it survives the hop to
    /// the assignment plane, a persisted progression row, or a monitoring UI. Null whenever the shard is
    /// not reporting a failure. Default null so existing implementations are unaffected.
    /// See GH-3637 / GH-3638 and JasperFx/jasperfx#565.</para>
    /// </summary>
    ShardFailure? Failure => null;

    /// <summary>
    /// GH-3888: whether this agent has burned through its budget of node-local auto-restarts without
    /// its sequence advancing. The auto-restart in <see cref="EventSubscriptionAgent" /> is node-local
    /// — nothing about a stop/start moves the shard anywhere — so when the fault is a property of the
    /// NODE (memory starvation, a bad disk, a wedged runtime) every retry re-runs the same conditions
    /// while a healthy peer advertising the same capability never gets a chance at the shard. An agent
    /// reporting <c>true</c> here is asking <see cref="NodeAgentController.ReportFailedLocalAgentsAsync" />
    /// to release its assignment so the leader can place it on another capable node. Any observed
    /// progress resets it. Default false so existing implementations are unaffected.
    /// </summary>
    bool LocalRestartsExhausted => false;

    /// <summary>
    /// GH-3888: give the node-local auto-restart loop a fresh budget. Called by the assignment plane
    /// when it declines to release an exhausted agent — no live peer advertises the capability, so
    /// retrying here remains the least-bad option and must keep happening rather than freezing the
    /// shard. Default no-op so existing implementations are unaffected.
    /// </summary>
    void ResetLocalRestartBudget()
    {
    }
}
