namespace Wolverine.Runtime.Agents;

/// <summary>
///     Pluggable model for managing the assignment and execution of stateful, "sticky"
///     background agents on the various nodes of a running Wolverine cluster
/// </summary>
public interface IAgentFamily
{
    /// <summary>
    ///     Uri scheme for this family of agents
    /// </summary>
    string Scheme { get; }

    /// <summary>
    ///     List of all the possible agents by their identity for this family of agents
    /// </summary>
    /// <returns></returns>
    ValueTask<IReadOnlyList<Uri>> AllKnownAgentsAsync();

    /// <summary>
    ///     Create or resolve the agent for this family
    /// </summary>
    /// <param name="uri"></param>
    /// <param name="wolverineRuntime"></param>
    /// <returns></returns>
    ValueTask<IAgent> BuildAgentAsync(Uri uri, IWolverineRuntime wolverineRuntime);

    /// <summary>
    ///     All supported agent uris by this node instance
    /// </summary>
    /// <returns></returns>
    ValueTask<IReadOnlyList<Uri>> SupportedAgentsAsync();

    /// <summary>
    ///     Assign agents to the currently running nodes when new nodes are detected or existing
    ///     nodes are deactivated
    /// </summary>
    /// <param name="assignments"></param>
    /// <returns></returns>
    ValueTask EvaluateAssignmentsAsync(AssignmentGrid assignments);
}