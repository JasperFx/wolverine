using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading;
using JasperFx.CodeGeneration;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Lamar;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.ObjectPool;
using Wolverine.Configuration;
using Wolverine.Logging;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime.Handlers;
using Wolverine.Runtime.RemoteInvocation;
using Wolverine.Runtime.Routing;
using Wolverine.Runtime.Scheduled;

namespace Wolverine.Runtime;

public sealed partial class WolverineRuntime : IWolverineRuntime, IHostedService
{
    private readonly IContainer _container;
    private readonly EndpointCollection _endpoints;
    private readonly LightweightCache<Type, IMessageInvoker> _invokers;

    private readonly Lazy<IMessageStore> _persistence;

    private readonly string _serviceName;
    private readonly int _uniqueNodeId;

    private ImHashMap<Type, object?> _extensions = ImHashMap<Type, object?>.Empty;
    private bool _hasStopped;
    


    public WolverineRuntime(WolverineOptions options,
        IContainer container,
        ILogger<WolverineRuntime> logger, IHostEnvironment environment)
    {
        DurabilitySettings = options.Durability;
        Options = options;
        Handlers = options.HandlerGraph;
        Environment = environment;
        
        Meter = new Meter("Wolverine:" + options.ServiceName, GetType().Assembly.GetName().Version?.ToString());
        logger.LogInformation("Exporting Open Telemetry metrics from Wolverine with name {Name}, version {Version}", Meter.Name, Meter.Version);

        Logger = logger;

        _uniqueNodeId = options.Durability.UniqueNodeId;
        _serviceName = options.ServiceName ?? "WolverineService";

        var provider = container.GetInstance<ObjectPoolProvider>();
        ExecutionPool = provider.Create(this);

        Pipeline = new HandlerPipeline(this, this);

        _persistence = new Lazy<IMessageStore>(container.GetInstance<IMessageStore>);

        _container = container;

        Cancellation = DurabilitySettings.Cancellation;

        ListenerTracker = new ListenerTracker(logger);

        _endpoints = new EndpointCollection(this);

        Replies = new ReplyTracker(logger);
        Handlers.AddMessageHandler(typeof(Acknowledgement), new AcknowledgementHandler(Replies));
        Handlers.AddMessageHandler(typeof(FailureAcknowledgement), new FailureAcknowledgementHandler(Replies));

        _sentCounter = Meter.CreateCounter<int>(MetricsConstants.MessagesSent, MetricsConstants.Messages,
            "Number of messages sent");
        _executionCounter = Meter.CreateHistogram<long>(MetricsConstants.ExecutionTime, MetricsConstants.Milliseconds,
            "Execution time in seconds");
        _successCounter = Meter.CreateCounter<int>(MetricsConstants.MessagesSucceeded, MetricsConstants.Messages,
            "Number of messages successfully processed");

        _failureCounter = Meter.CreateCounter<int>(MetricsConstants.MessagesFailed, MetricsConstants.Messages,
            "Number of message execution failures");
        
        _deadLetterQueueCounter = Meter.CreateCounter<int>(MetricsConstants.DeadLetterQueue, MetricsConstants.Messages,
            "Number of messages moved to dead letter queues");

        _effectiveTime = Meter.CreateHistogram<double>(MetricsConstants.EffectiveMessageTime,
            MetricsConstants.Milliseconds,
            "Effective time between a message being sent and being completely handled");
        
        _invokers = new(findInvoker);
    }
    
    private IMessageInvoker findInvoker(Type messageType)
    {
        try
        {
            if (Options.HandlerGraph.CanHandle(messageType))
            {
                return this.As<IExecutorFactory>().BuildFor(messageType);
            }
            
            return (IMessageInvoker)RoutingFor(messageType).FindSingleRouteForSending();
        }
        catch (Exception e)
        {
            Logger.LogError(e, "Failed to create a message handler for {MessageType}", messageType.FullNameInCode());
            return new NoHandlerExecutor(messageType, this){ExceptionText = e.ToString()};
        }
    }
    
    public IMessageInvoker FindInvoker(Type messageType)
    {
        return _invokers[messageType];
    }

    public Meter Meter { get; }

    public ObjectPool<MessageContext> ExecutionPool { get; }

    internal IDurabilityAgent? Durability { get; private set; }

    internal HandlerGraph Handlers { get; }

    internal IScheduledJobProcessor ScheduledJobs { get; private set; } = null!;

    public IReplyTracker Replies { get; }

    public IEndpointCollection Endpoints => _endpoints;

    public ListenerTracker ListenerTracker { get; }

    public CancellationToken Cancellation { get; }

    public T? TryFindExtension<T>() where T : class
    {
        if (_extensions.TryFind(typeof(T), out var raw))
        {
            return raw as T;
        }

        var extension = Options.AppliedExtensions.OfType<T>().FirstOrDefault();
        _extensions = _extensions.AddOrUpdate(typeof(T), extension);

        return extension;
    }

    public DurabilitySettings DurabilitySettings { get; }

    public ILogger Logger { get; }

    public WolverineOptions Options { get; }

    public void ScheduleLocalExecutionInMemory(DateTimeOffset executionTime, Envelope envelope)
    {
        MessageLogger.Sent(envelope);
        ScheduledJobs.Enqueue(executionTime, envelope);
    }

    public IHostEnvironment Environment { get; }

    public IHandlerPipeline Pipeline { get; }

    public IMessageLogger MessageLogger => this;


    public IMessageStore Storage => _persistence.Value;

    internal IReadOnlyList<IMissingHandler> MissingHandlers()
    {
        return _container.GetAllInstances<IMissingHandler>();
    }
}