using JasperFx.CodeGeneration.Model;
using Wolverine.Runtime.Handlers;

namespace Wolverine.Configuration;

/// <summary>
/// Given a return variable from a handler method, return a Frame
/// for how Wolverine should handle that return value
/// </summary>
public interface IReturnVariableActionSource
{
    IReturnVariableAction Build(IChain chain, Variable variable);
}

internal class CascadingMessageActionSource : IReturnVariableActionSource
{
    public IReturnVariableAction Build(IChain chain, Variable variable)
    {
        return new CascadeMessage(variable);
    }
}

