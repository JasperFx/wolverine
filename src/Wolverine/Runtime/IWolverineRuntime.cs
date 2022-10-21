using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Wolverine.Runtime.Scheduled;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.ObjectPool;
using Wolverine.Configuration;
using Wolverine.Logging;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime.Handlers;
using Wolverine.Runtime.ResponseReply;
using Wolverine.Runtime.Routing;


namespace Wolverine.Runtime;

public interface IWolverineRuntime
{
    /// <summary>
    /// Schedule an envelope for later execution in memory
    /// </summary>
    /// <param name="executionTime"></param>
    /// <param name="envelope"></param>
    void ScheduleLocalExecutionInMemory(DateTimeOffset executionTime, Envelope envelope);
    
    IHostEnvironment Environment { get; }

    IHandlerPipeline Pipeline { get; }
    IMessageLogger MessageLogger { get; }
    WolverineOptions Options { get; }

    IEnvelopePersistence Persistence { get; }
    ILogger Logger { get; }
    AdvancedSettings Advanced { get; }
    CancellationToken Cancellation { get; }
    ListenerTracker ListenerTracker { get; }
    
    IReplyTracker Replies { get; }  
    IEndpointCollection Endpoints { get; }


    IMessageRouter RoutingFor(Type messageType);

    /// <summary>
    /// Try to find an applied extension of type T
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    T? TryFindExtension<T>() where T : class;


    void RegisterMessageType(Type messageType);
}

internal interface IExecutorFactory
{
    IExecutor BuildFor(Type messageType);
}

// This was for testing
internal static class WolverineRuntimeExtensions
{
    /// <summary>
    /// Shortcut to preview the routing for a single message
    /// </summary>
    /// <param name="runtime"></param>
    /// <param name="message"></param>
    /// <param name="options"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    public static Envelope[] RouteForSend(this IWolverineRuntime runtime, object message, DeliveryOptions? options)
    {
        if (message == null)
        {
            throw new ArgumentNullException(nameof(message));
        }

        var router = runtime.RoutingFor(message.GetType());
        return router.RouteForSend(message, options);
    }
}

