using JasperFx.Core.Reflection;
using Microsoft.Extensions.Logging;
using Wolverine.Runtime.Agents;

namespace Wolverine.Runtime;

/// <summary>
/// What actually happened to an agent command routed by
/// <see cref="AgentMessagingExtensions.InvokeOnAgentAsync(IMessageContext,Uri,Func{IWolverineRuntime,CancellationToken,Task},CancellationToken)"/>.
/// </summary>
/// <remarks>
/// This exists because a <c>bool</c> cannot carry it. "Executed here" and "handed to another node"
/// are both successes for the caller, but only the first means the work is done — and the two
/// failures below are not the same failure and do not want the same fallback.
/// </remarks>
public enum AgentInvocationOutcome
{
    /// <summary>The action ran on this node. The work is done.</summary>
    ExecutedLocally,

    /// <summary>Another node owns the agent and the message was sent to it. The work is not done yet.</summary>
    Forwarded,

    /// <summary>No node in the durable node table claims the agent.</summary>
    NoOwner,

    /// <summary>
    /// The node table says THIS node owns the agent, but it is not running here.
    /// </summary>
    /// <remarks>
    /// The routing decision and the execution decision read two different sources — the durable
    /// <c>wolverine_nodes</c> table and the in-process <c>NodeController.Agents</c> dictionary — and
    /// during a startup window the table is populated while the dictionary is still empty. There is
    /// nothing useful to do with the message here: forwarding it would send it to the node it is
    /// already on, which is a silent no-op. Callers should fall back or fail honestly.
    /// </remarks>
    NotRunningLocally
}

/// <summary>
/// Extension methods for routing agent commands to the correct node in a Wolverine cluster.
/// </summary>
public static class AgentMessagingExtensions
{
    /// <summary>
    /// Executes an action locally if the specified agent is running on this node, otherwise forwards
    /// the current message to the node that owns the agent, and reports which of those happened.
    /// </summary>
    /// <param name="context">The current message context.</param>
    /// <param name="agentUri">The URI identifying the target agent.</param>
    /// <param name="action">The action to execute if the agent is local to this node.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task<AgentInvocationOutcome> InvokeOnAgentAsync(this IMessageContext context, Uri agentUri,
        Func<IWolverineRuntime, CancellationToken, Task> action, CancellationToken cancellationToken)
    {
        var messageContext = context.As<MessageContext>();
        var runtime = messageContext.Runtime;

        if (runtime.Agents.AllRunningAgentUris().Contains(agentUri))
        {
            await action(runtime, cancellationToken);
            return AgentInvocationOutcome.ExecutedLocally;
        }

        var all = await runtime.Storage.Nodes.LoadAllNodesAsync(cancellationToken);
        var node = all.FirstOrDefault(x => x.ActiveAgents.Contains(agentUri));

        if (node == null) return AgentInvocationOutcome.NoOwner;

        // Never forward to ourselves. The check above asked the in-process agent dictionary and this
        // one asks the durable node table; when they disagree — the table already claims the agent
        // while the dictionary is still filling during startup — sending the envelope to
        // node.ControlUri delivers it back to this node, where it takes the same branch again. The
        // caller was previously told "true" for that, so a command that did nothing at all was
        // reported as handled. FanOutToAllNodes below has always excluded self for the same reason.
        if (node.NodeId == runtime.Options.UniqueNodeId)
        {
            runtime.Logger.LogWarning(
                "Node {NodeId} is recorded as the owner of agent {AgentUri} in node storage, but that agent is not running on this node. Not forwarding the message to ourselves.",
                node.NodeId, agentUri);

            return AgentInvocationOutcome.NotRunningLocally;
        }

        if (node.ControlUri == null)
        {
            runtime.Logger.LogWarning(
                "Node {NodeId} owns agent {AgentUri} but has no control endpoint, so the message cannot be forwarded to it",
                node.NodeId, agentUri);

            return AgentInvocationOutcome.NoOwner;
        }

        await messageContext.EndpointFor(node.ControlUri).SendAsync(context.Envelope!.Message);
        return AgentInvocationOutcome.Forwarded;
    }

    /// <summary>
    /// Executes an action on a specific running agent (cast to type T) if the agent is on this node,
    /// otherwise forwards the current message to the node that owns the agent, and reports which of
    /// those happened.
    /// </summary>
    /// <typeparam name="T">The expected agent type implementing IAgent.</typeparam>
    /// <param name="context">The current message context.</param>
    /// <param name="agentUri">The URI identifying the target agent.</param>
    /// <param name="action">The action to execute on the typed agent if found locally.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static Task<AgentInvocationOutcome> InvokeOnAgentAsync<T>(this IMessageContext context, Uri agentUri,
        Func<T, Task> action, CancellationToken cancellationToken) where T : class, IAgent
    {
        return context.InvokeOnAgentAsync(agentUri, async (runtime, ct) =>
        {
            if (runtime.Agents.TryFindActiveAgent<T>(agentUri, out var agent))
            {
                await action(agent);
            }
            else
            {
                runtime.Logger.LogWarning(
                    "Agent at {AgentUri} was expected to be of type {ExpectedType} but was not found or is a different type",
                    agentUri, typeof(T).Name);
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Executes an action locally if the specified agent is running on this node,
    /// otherwise forwards the current message to the node that owns the agent.
    /// </summary>
    /// <param name="context">The current message context.</param>
    /// <param name="agentUri">The URI identifying the target agent.</param>
    /// <param name="action">The action to execute if the agent is local to this node.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <c>true</c> if the action was executed locally or the message was successfully forwarded;
    /// <c>false</c> if the command could not be delivered to a running agent.
    /// </returns>
    /// <remarks>
    /// ⚠️ A caller that needs to know whether the WORK IS DONE cannot use this — <c>true</c> also
    /// means "handed to another node, which will do it later". Use
    /// <see cref="InvokeOnAgentAsync(IMessageContext,Uri,Func{IWolverineRuntime,CancellationToken,Task},CancellationToken)"/>
    /// and read <see cref="AgentInvocationOutcome"/>.
    /// </remarks>
    public static async Task<bool> InvokeOnAgentOrForwardAsync(this IMessageContext context, Uri agentUri,
        Func<IWolverineRuntime, CancellationToken, Task> action, CancellationToken cancellationToken)
    {
        var outcome = await context.InvokeOnAgentAsync(agentUri, action, cancellationToken);
        return outcome is AgentInvocationOutcome.ExecutedLocally or AgentInvocationOutcome.Forwarded;
    }

    /// <summary>
    /// Executes an action on a specific running agent (cast to type T) if the agent is on this node,
    /// otherwise forwards the current message to the node that owns the agent.
    /// </summary>
    /// <typeparam name="T">The expected agent type implementing IAgent.</typeparam>
    /// <param name="context">The current message context.</param>
    /// <param name="agentUri">The URI identifying the target agent.</param>
    /// <param name="action">The action to execute on the typed agent if found locally.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <c>true</c> if the action was executed locally or the message was successfully forwarded;
    /// <c>false</c> if the command could not be delivered to a running agent.
    /// </returns>
    public static async Task<bool> InvokeOnAgentOrForwardAsync<T>(this IMessageContext context, Uri agentUri,
        Func<T, Task> action, CancellationToken cancellationToken) where T : class, IAgent
    {
        var outcome = await context.InvokeOnAgentAsync(agentUri, action, cancellationToken);
        return outcome is AgentInvocationOutcome.ExecutedLocally or AgentInvocationOutcome.Forwarded;
    }

    /// <summary>
    /// Publishes a message locally and sends it to every other node in the cluster
    /// via each node's control URI. Node data is loaded fresh from persistence (no caching).
    /// </summary>
    /// <param name="context">The current message context.</param>
    /// <param name="message">The message to fan out to all nodes.</param>
    public static async Task FanOutToAllNodes(this IMessageContext context, object message)
    {
        var messageContext = context.As<MessageContext>();
        var runtime = messageContext.Runtime;

        // Publish locally
        await messageContext.PublishAsync(message);

        // Load all nodes fresh from persistence
        var allNodes = await runtime.Storage.Nodes.LoadAllNodesAsync(CancellationToken.None);

        // Send to every other node via its ControlUri
        var selfId = runtime.Options.UniqueNodeId;
        foreach (var node in allNodes)
        {
            if (node.NodeId != selfId && node.ControlUri != null)
            {
                await messageContext.EndpointFor(node.ControlUri).SendAsync(message);
            }
        }
    }
}
