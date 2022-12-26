using Wolverine.Runtime;

namespace Wolverine.Configuration;

public class MessageRoutingContext
{
    public MessageRoutingContext(Type messageType, IWolverineRuntime runtime)
    {
        MessageType = messageType;
        Runtime = runtime;
    }

    public Type MessageType { get; }
    public IWolverineRuntime Runtime { get; }
}