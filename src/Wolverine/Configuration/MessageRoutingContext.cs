using System;
using Wolverine.Runtime;

namespace Wolverine.Configuration;

public class MessageRoutingContext
{
    public Type MessageType { get; }
    public IWolverineRuntime Runtime { get; }

    public MessageRoutingContext(Type messageType, IWolverineRuntime runtime)
    {
        MessageType = messageType;
        Runtime = runtime;
    }


}