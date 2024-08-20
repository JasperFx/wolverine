using Wolverine.Configuration;
using Wolverine.Runtime.Handlers;

namespace Wolverine.Runtime;

public partial class WolverineRuntime : IExecutorFactory
{
    IExecutor IExecutorFactory.BuildFor(Type messageType)
    {
        var executor = Executor.Build(this, ExecutionPool, Handlers, messageType);

        return executor;
    }

    IExecutor IExecutorFactory.BuildFor(Type messageType, Endpoint endpoint)
    {
        var handler = Handlers.HandlerFor(messageType, endpoint);
        if (handler == null )
        {
            var batching = Options.BatchDefinitions.FirstOrDefault(x => x.ElementType == messageType);
            if (batching != null)
            {
                handler = batching.BuildHandler(this);
            }
        }
        
        var executor = handler == null
            ? new NoHandlerExecutor(messageType, this)
            : Executor.Build(this, ExecutionPool, Handlers, handler);

        return executor;
    }
}