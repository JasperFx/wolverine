using Wolverine.Runtime.Agents;

namespace Wolverine.Runtime;

public interface IAgentRuntime
{
    Task StartLocallyAsync(Uri agentUri);
    Task StopLocallyAsync(Uri agentUri);

    Task InvokeAsync(NodeDestination destination, IAgentCommand command);

    Task<T> InvokeAsync<T>(NodeDestination destination, IAgentCommand command) where T : class;
    Uri[] AllRunningAgentUris();

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