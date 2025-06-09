using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Logging;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime.Agents;
using Wolverine.Runtime.Handlers;
using Wolverine.Runtime.RemoteInvocation;
using Wolverine.Runtime.Routing;

namespace Wolverine.Runtime;

public interface IWolverineRuntime
{
    IHandlerPipeline Pipeline { get; }
    IMessageTracker MessageTracking { get; }
    WolverineOptions Options { get; }

    IMessageStore Storage { get; }
    ILogger Logger { get; }
    DurabilitySettings DurabilitySettings { get; }
    CancellationToken Cancellation { get; }
    WolverineTracker Tracker { get; }

    IReplyTracker Replies { get; }
    IEndpointCollection Endpoints { get; }
    Meter Meter { get; }
    ILoggerFactory LoggerFactory { get; }

    IAgentRuntime Agents { get; }
    IReadOnlyList<IAncillaryMessageStore> AncillaryStores { get; }
    IServiceProvider Services { get; }
    
    IWolverineObserver Observer { get; set; }

    /// <summary>
    ///     Schedule an envelope for later execution in memory
    /// </summary>
    /// <param name="executionTime"></param>
    /// <param name="envelope"></param>
    void ScheduleLocalExecutionInMemory(DateTimeOffset executionTime, Envelope envelope);


    IMessageRouter RoutingFor(Type messageType);

    /// <summary>
    ///     Try to find an applied extension of type T
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    T? TryFindExtension<T>() where T : class;


    void RegisterMessageType(Type messageType);

    IMessageInvoker FindInvoker(Type messageType);
    void AssertHasStarted();
    IMessageInvoker FindInvoker(string envelopeMessageType);
}

public record NodeDestination(Guid NodeId, Uri ControlUri)
{
    public static NodeDestination Empty() => new NodeDestination(Guid.Empty, new Uri("null://null"));

    public static NodeDestination Standin() => new NodeDestination(Guid.NewGuid(), new Uri("tcp://1000"));
}

public interface IExecutorFactory
{
    IExecutor BuildFor(Type messageType);
    IExecutor BuildFor(Type messageType, Endpoint endpoint);
}

// This was for testing
internal static class WolverineRuntimeExtensions
{
    /// <summary>
    ///     Shortcut to preview the routing for a single message
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