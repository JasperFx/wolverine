using System;
using System.Threading;
using System.Threading.Tasks;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Wolverine.Runtime.Handlers;

namespace Wolverine.Runtime.Routing;

public class MessageInvokerCollection
{
    private readonly WolverineRuntime _runtime;
    private readonly LightweightCache<Type, IMessageInvoker> _invokers;

    public MessageInvokerCollection(IWolverineRuntime runtime)
    {
        _runtime = (WolverineRuntime)runtime;
        
        _invokers = new(findInvoker);
    }

    internal IMessageInvoker FindForMessageType(Type messageType)
    {
        return _invokers[messageType];
    }

    private IMessageInvoker findInvoker(Type messageType)
    {
        try
        {
            if (_runtime.Options.HandlerGraph.CanHandle(messageType))
            {
                return _runtime.As<IExecutorFactory>().BuildFor(messageType);
            }
            
            return (IMessageInvoker)_runtime.RoutingFor(messageType).FindSingleRouteForSending();
        }
        catch (Exception)
        {
            return new NoHandlerExecutor(messageType, _runtime);
        }
    }
}


internal interface IMessageInvoker
{
    Task<T> InvokeAsync<T>(object message, MessageBus bus,
        CancellationToken cancellation = default,
        TimeSpan? timeout = null) where T : class;
    
    Task InvokeAsync(object message, MessageBus bus,
        CancellationToken cancellation = default,
        TimeSpan? timeout = null);
}