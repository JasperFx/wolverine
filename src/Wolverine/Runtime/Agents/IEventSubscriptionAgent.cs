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
}
