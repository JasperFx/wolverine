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