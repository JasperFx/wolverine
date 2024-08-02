using Wolverine.Configuration;
using Wolverine.Runtime.Handlers;

namespace Wolverine.Runtime;

public partial class WolverineRuntime : IExecutorFactory
{
    IExecutor IExecutorFactory.BuildFor(Type messageType)
    {
        var handler = Handlers.HandlerFor(messageType);
        var executor = handler == null
            ? new NoHandlerExecutor(messageType, this)
            : Executor.Build(this, ExecutionPool, Handlers, messageType);

        return executor;
    }

    IExecutor IExecutorFactory.BuildFor(Type messageType, Endpoint endpoint)
    {
        var handler = Handlers.HandlerFor(messageType, endpoint);
        var executor = handler == null
            ? new NoHandlerExecutor(messageType, this)
            : Executor.Build(this, ExecutionPool, Handlers, messageType);

        return executor;
    }
}