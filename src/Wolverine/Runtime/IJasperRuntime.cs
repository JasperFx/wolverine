using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wolverine.Runtime.Scheduled;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.ObjectPool;
using Wolverine.Configuration;
using Wolverine.Logging;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime.Handlers;
using Wolverine.Runtime.Routing;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.Runtime;

public interface IWolverineRuntime
{
    /// <summary>
    /// Schedule an envelope for later execution in memory
    /// </summary>
    /// <param name="executionTime"></param>
    /// <param name="envelope"></param>
    void ScheduleLocalExecutionInMemory(DateTimeOffset executionTime, Envelope envelope);

    IHandlerPipeline Pipeline { get; }
    IMessageLogger MessageLogger { get; }
    WolverineOptions Options { get; }

    IEnvelopePersistence Persistence { get; }
    ILogger Logger { get; }
    AdvancedSettings Advanced { get; }
    CancellationToken Cancellation { get; }
    ListenerTracker ListenerTracker { get; }

    ISendingAgent CreateSendingAgent(Uri? replyUri, ISender sender, Endpoint endpoint);

    ISendingAgent GetOrBuildSendingAgent(Uri address, Action<Endpoint>? configureNewEndpoint = null);

    IEnumerable<IListeningAgent> ActiveListeners();

    void AddSendingAgent(ISendingAgent sendingAgent);

    Endpoint? EndpointFor(Uri uri);
    IMessageRouter RoutingFor(Type messageType);

    /// <summary>
    /// Try to find an applied extension of type T
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    T? TryFindExtension<T>() where T : class;

    /// <summary>
    /// Try to find a listening agent by Uri
    /// </summary>
    /// <param name="uri"></param>
    /// <returns></returns>
    IListeningAgent? FindListeningAgent(Uri uri);

    /// <summary>
    /// Try to find a listening agent by endpoint name
    /// </summary>
    /// <param name="endpointName"></param>
    /// <returns></returns>
    IListeningAgent? FindListeningAgent(string endpointName);


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

