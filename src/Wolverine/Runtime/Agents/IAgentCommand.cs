using JasperFx.Core.Reflection;
using System.Diagnostics.CodeAnalysis;

namespace Wolverine.Runtime.Agents;

/// <summary>
///     Used for self-contained actions from agents to be executed in background, local
///     queues with Wolverine infrastructure
/// </summary>
public interface IAgentCommand
{
    Task<AgentCommands> ExecuteAsync(IWolverineRuntime runtime, CancellationToken cancellationToken);

    /// <summary>
    ///     The node this command dispatches work to, when it targets exactly one node. The leader's agent
    ///     command drain runs commands one at a time <i>per destination</i> while letting different
    ///     destinations proceed concurrently, so a node that is slow to start its share of the agents no
    ///     longer holds up every other node in the cluster (GH-3698). Return null — the default — when the
    ///     command targets no particular node or more than one; those run in a single strictly-serial lane,
    ///     exactly as the whole drain used to.
    /// </summary>
    Guid? DestinationNodeId => null;
}

public class AgentCommands : List<IAgentCommand>, ISendMyself
{
    // Must hand out a fresh instance every time. AgentCommands is a mutable
    // List<IAgentCommand> whose mutation members are non-virtual, so a cached
    // singleton cannot be guarded — any caller that Adds/Pops on a returned
    // Empty would poison it for every subsequent caller process-wide.
    public static AgentCommands Empty => new();

    public async ValueTask ApplyAsync(IMessageContext context)
    {
        foreach (var command in this)
        {
            await context.PublishAsync(command);
        }
    }

    public IAgentCommand Pop()
    {
        var command = this[0];
        Remove(command);
        return command;
    }
}

internal class AgentCommandHandledTypeRule : IHandledTypeRule
{
    public bool TryFindHandledType(Type concreteType, [NotNullWhen(true)] out Type? handlerType)
    {
        if (concreteType.CanBeCastTo(typeof(IAgentCommand)))
        {
            handlerType = typeof(IAgentCommand);
            return true;
        }

        handlerType = null;
        return false;
    }
}