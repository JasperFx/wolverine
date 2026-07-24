using JasperFx.Descriptors;
using Wolverine.Runtime.Agents;

namespace Wolverine.Runtime;

public interface IAgentRuntime
{
    Task StartLocallyAsync(Uri agentUri);
    Task StopLocallyAsync(Uri agentUri);

    Task InvokeAsync(NodeDestination destination, IAgentCommand command);

    Task<T> InvokeAsync<T>(NodeDestination destination, IAgentCommand command, TimeSpan? timeout = null) where T : class;
    Uri[] AllRunningAgentUris();

    /// <summary>
    /// The distinct set of shard databases whose event-subscription / projection agents are currently
    /// assigned to and running on THIS node under Wolverine-managed event subscription distribution
    /// (<c>UseWolverineManagedEventSubscriptionDistribution = true</c>). Under a
    /// <c>MultiTenantedWithShardedDatabases</c> store with database-affine assignment, a node only runs
    /// the daemon for the databases it owns; use this to scope per-node work (readiness gates, health
    /// checks, progress queries) to exactly those databases instead of fanning out a connection pool to
    /// every shard. Returns an empty list on nodes that own no such agents. See GH-3340.
    /// </summary>
    IReadOnlyList<DatabaseId> AllLocallyOwnedDatabaseIds();

    /// <summary>
    /// Use with caution! This will force Wolverine into restarting its leadership
    /// election and agent assignment
    /// </summary>
    /// <returns></returns>
    Task KickstartHealthDetectionAsync();

    Task<AgentCommands> DoHealthChecksAsync();

    /// <summary>
    /// ONLY FOR TESTING! Disables all health check monitoring and heart beats
    /// </summary>
    /// <returns></returns>
    void DisableHealthChecks();

    /// <summary>
    /// Applies new agent restrictions
    /// </summary>
    /// <param name="restrictions"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task ApplyRestrictionsAsync(AgentRestrictions restrictions, CancellationToken cancellationToken);

    /// <summary>
    /// Try to find an actively running agent of the type T
    /// </summary>
    /// <param name="agentUri"></param>
    /// <param name="agent"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    bool TryFindActiveAgent<T>(Uri agentUri, out T agent) where T : class;
}