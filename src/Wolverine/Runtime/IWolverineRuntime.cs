using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.ErrorHandling;
using Wolverine.Logging;
using Wolverine.Persistence;
using Wolverine.Persistence.Durability;
using Wolverine.Persistence.Sagas;
using Wolverine.Runtime.Agents;
using Wolverine.Runtime.Handlers;
using Wolverine.Runtime.Metrics;
using Wolverine.Runtime.RemoteInvocation;
using Wolverine.Runtime.Routing;
using Wolverine.Runtime.Stubs;

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
    
    MetricsAccumulator MetricsAccumulator { get; }
    
    ILoggerFactory LoggerFactory { get; }

    IAgentRuntime Agents { get; }
    IServiceProvider Services { get; }
    
    IWolverineObserver Observer { get; set; }
    MessageStoreCollection Stores { get; }

    /// <summary>
    /// Read-only diagnostic surface over every saga storage registered
    /// with this Wolverine application. Wraps Marten, EF Core, RavenDB,
    /// or any other registered <see cref="ISagaStoreDiagnostics"/>
    /// implementation behind a single fan-out aggregator so callers
    /// (CritterWatch and other monitoring tools) can list saga types,
    /// fetch a saga instance by id, or peek at recent instances without
    /// caring which store actually holds the data.
    /// </summary>
    ISagaStoreDiagnostics SagaStorage { get; }

    /// <summary>
    /// Try to find the main message store in a completely initialized state and safely cast to the type "T"
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    Task<T?> TryFindMainMessageStore<T>() where T : class;

    IMessageStore FindAncillaryStoreForMarkerType(Type markerType);

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
    
    /// <summary>
    /// Use this to temporarily add message handling stubs to take the place of external systems in testing
    /// that may be sending replies back to your application
    /// </summary>
    IStubHandlers Stubs { get; }

    /// <summary>
    /// Immediately enqueue these envelopes for execution in the listening circuit
    /// designated by the Envelope.Destination, or the default local queue if none exists
    ///
    /// This skips all inbox functions. Mainly for scheduled message execution
    /// </summary>
    /// <param name="envelopes"></param>
    /// <returns></returns>
    ValueTask EnqueueDirectlyAsync(IReadOnlyList<Envelope> envelopes);
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

internal interface IWolverineRuntimeInternal : IWolverineRuntime
{
    IFaultPublisher FaultPublisher { get; }
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

    internal static ValueTask PublishFaultIfEnabledAsync(
        this IWolverineRuntime runtime,
        IEnvelopeLifecycle lifecycle,
        Exception exception,
        FaultTrigger trigger,
        System.Diagnostics.Activity? activity)
        => runtime is IWolverineRuntimeInternal wri
            ? wri.FaultPublisher.PublishIfEnabledAsync(lifecycle, exception, trigger, activity)
            : ValueTask.CompletedTask;
}