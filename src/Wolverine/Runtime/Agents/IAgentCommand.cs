using JasperFx.Core.Reflection;

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
    public static AgentCommands Empty { get; } = new();

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
    public bool TryFindHandledType(Type concreteType, out Type handlerType)
    {
        if (concreteType.CanBeCastTo(typeof(IAgentCommand)))
        {
            handlerType = typeof(IAgentCommand);
            return true;
        }

        handlerType = default;
        return false;
    }
}