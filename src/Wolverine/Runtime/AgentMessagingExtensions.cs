using JasperFx.Core.Reflection;

namespace Wolverine.Runtime;

/// <summary>
/// Extension methods for routing agent commands to the correct node in a Wolverine cluster.
/// </summary>
public static class AgentMessagingExtensions
{
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
    /// <c>false</c> if no node currently owns the specified agent.
    /// </returns>
    public static async Task<bool> InvokeOnAgentOrForwardAsync(this IMessageContext context, Uri agentUri,
        Func<IWolverineRuntime, CancellationToken, Task> action, CancellationToken cancellationToken)
    {
        var messageContext = context.As<MessageContext>();
        var runtime = messageContext.Runtime;

        if (runtime.Agents.AllRunningAgentUris().Contains(agentUri))
        {
            await action(runtime, cancellationToken);
            return true;
        }

        var all = await runtime.Storage.Nodes.LoadAllNodesAsync(cancellationToken);
        var node = all.FirstOrDefault(x => x.ActiveAgents.Contains(agentUri));

        if (node == null) return false;

        await messageContext.EndpointFor(node!.ControlUri!).SendAsync(context.Envelope!.Message);
        return true;
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