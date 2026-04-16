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
}
