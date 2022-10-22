using System;
using Wolverine.Runtime.Handlers;

namespace Wolverine.Runtime;

public partial class WolverineRuntime : IExecutorFactory
{
    IExecutor IExecutorFactory.BuildFor(Type messageType)
    {
        var handler = Handlers.HandlerFor(messageType);
        var executor = handler == null
            ? new NoHandlerExecutor(messageType, this)
            : (IExecutor)Executor.Build(this, Handlers, messageType);

        return executor!;
    }
}
