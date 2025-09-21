using System.Diagnostics.Metrics;
using ImTools;
using JasperFx;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.ObjectPool;
using Wolverine.Configuration;
using Wolverine.Logging;
using Wolverine.Persistence;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime.Agents;
using Wolverine.Runtime.Handlers;
using Wolverine.Runtime.RemoteInvocation;
using Wolverine.Runtime.Routing;
using Wolverine.Runtime.Scheduled;

namespace Wolverine.Runtime;

public sealed partial class WolverineRuntime : IWolverineRuntime, IHostedService
{
    private readonly IServiceContainer _container;
    private readonly EndpointCollection _endpoints;
    private readonly LightweightCache<Type, IMessageInvoker> _invokers;

    private readonly string _serviceName;
    private readonly Guid _uniqueNodeId;

    private ImHashMap<Type, object?> _extensions = ImHashMap<Type, object?>.Empty;
    private bool _hasStopped;

    private readonly Lazy<MessageStoreCollection> _stores;

    public WolverineRuntime(WolverineOptions options,
        IServiceContainer container,
        ILoggerFactory loggers)
    {
        DurabilitySettings = options.Durability;
        Options = options;
        Handlers = options.HandlerGraph;

        _stores =
            new Lazy<MessageStoreCollection>(() => container.Services.GetRequiredService<MessageStoreCollection>());

        LoggerFactory = loggers;
        Logger = loggers.CreateLogger<WolverineRuntime>();
        
        Observer = new PersistenceWolverineObserver(this);

        Meter = new Meter("Wolverine:" + options.ServiceName, GetType().Assembly.GetName().Version?.ToString());
        Logger.LogInformation("Exporting Open Telemetry metrics from Wolverine with name {Name}, version {Version}",
            Meter.Name, Meter.Version);

        _uniqueNodeId = options.UniqueNodeId;
        _serviceName = options.ServiceName;

        var provider = container.GetInstance<ObjectPoolProvider>();
        ExecutionPool = provider.Create(this);

        Pipeline = new HandlerPipeline(this, this);

        _container = container;

        Cancellation = DurabilitySettings.Cancellation;
        _agentCancellation = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);

        Tracker = new WolverineTracker(Logger);

        _endpoints = new EndpointCollection(this);

        Replies = new ReplyTracker(loggers.CreateLogger<ReplyTracker>(), DurabilitySettings.AssignedNodeNumber);
        Handlers.AddMessageHandler(typeof(Acknowledgement), new AcknowledgementHandler(Replies));
        Handlers.AddMessageHandler(typeof(FailureAcknowledgement), new FailureAcknowledgementHandler(Replies, LoggerFactory.CreateLogger<FailureAcknowledgementHandler>()));

        _sentCounter = Meter.CreateCounter<int>(MetricsConstants.MessagesSent, MetricsConstants.Messages,
            "Number of messages sent");
        _executionCounter = Meter.CreateHistogram<long>(MetricsConstants.ExecutionTime, MetricsConstants.Milliseconds,
            "Execution time in milliseconds");
        _successCounter = Meter.CreateCounter<int>(MetricsConstants.MessagesSucceeded, MetricsConstants.Messages,
            "Number of messages successfully processed");

        _failureCounter = Meter.CreateCounter<int>(MetricsConstants.MessagesFailed, MetricsConstants.Messages,
            "Number of message execution failures");

        _deadLetterQueueCounter = Meter.CreateCounter<int>(MetricsConstants.DeadLetterQueue, MetricsConstants.Messages,
            "Number of messages moved to dead letter queues");

        _effectiveTime = Meter.CreateHistogram<double>(MetricsConstants.EffectiveMessageTime,
            MetricsConstants.Milliseconds,
            "Effective time between a message being sent and being completely handled in milliseconds");

        _invokers = new LightweightCache<Type, IMessageInvoker>(findInvoker);

        var activators = container.GetAllInstances<IWolverineActivator>();
        foreach (var activator in activators)
        {
            activator.Apply(this);
        }
    }

    public IWolverineObserver Observer { get; set; }

    public IServiceProvider Services => _container.Services;

    public ObjectPool<MessageContext> ExecutionPool { get; }

    internal HandlerGraph Handlers { get; }

    internal IScheduledJobProcessor ScheduledJobs { get; private set; } = null!;

    public IMessageInvoker FindInvoker(Type messageType)
    {
        return _invokers[messageType];
    }

    public IMessageInvoker FindInvoker(string envelopeMessageType)
    {
        if (Handlers.TryFindMessageType(envelopeMessageType, out var messageType))
        {
            return FindInvoker(messageType);
        }

        return new NulloMessageInvoker();
    }
    
    internal class NulloMessageInvoker : IMessageInvoker
    {
        public Task<T> InvokeAsync<T>(object message, MessageBus bus, CancellationToken cancellation = default, TimeSpan? timeout = null,
            string? tenantId = null)
        {
            throw new NotSupportedException();
        }

        public Task InvokeAsync(object message, MessageBus bus, CancellationToken cancellation = default, TimeSpan? timeout = null,
            string? tenantId = null)
        {
            return Task.CompletedTask;
        }
    }

    public void AssertHasStarted()
    {
        if (!_hasStarted)
        {
            throw new WolverineHasNotStartedException();
        }
    }

    public ILoggerFactory LoggerFactory { get; }

    public Meter Meter { get; }

    public IReplyTracker Replies { get; }

    public IEndpointCollection Endpoints => _endpoints;

    public WolverineTracker Tracker { get; }

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
        if (ScheduledJobs == null)
            throw new InvalidOperationException(
                $"This action is invalid when {nameof(WolverineOptions)}.{nameof(WolverineOptions.Durability)}.{nameof(DurabilitySettings.Mode)} = {Options.Durability.Mode}");

        MessageTracking.Sent(envelope);
        ScheduledJobs.Enqueue(executionTime, envelope);
    }

    public IMessageStore FindAncillaryStoreForMarkerType(Type markerType)
    {
        return _stores.Value.FindAncillaryStore(markerType);
    }

    public MessageStoreCollection Stores => _stores.Value;

    public async Task<T?> TryFindMainMessageStore<T>() where T : class
    {
        await _stores.Value.InitializeAsync();
        return _stores.Value.Main as T;
    }

    public IHandlerPipeline Pipeline { get; }

    public IMessageTracker MessageTracking => this;

    public IMessageStore Storage => _stores.Value.Main;

    private IMessageInvoker findInvoker(Type messageType)
    {
        try
        {
            foreach (var rule in Options.HandledTypeRules)
            {
                if (rule.TryFindHandledType(messageType, out var handledType))
                {
                    return this.As<IExecutorFactory>().BuildFor(handledType);
                }
            }

            if (Options.HandlerGraph.CanHandle(messageType))
            {
                return this.As<IExecutorFactory>().BuildFor(messageType);
            }

            return (IMessageInvoker)RoutingFor(messageType).FindSingleRouteForSending();
        }
        catch (Exception e)
        {
            Logger.LogError(e, "Failed to create a message handler for {MessageType}", messageType.FullNameInCode());
            return new NoHandlerExecutor(messageType, this) { Exception = e };
        }
    }

    internal IReadOnlyList<IMissingHandler> MissingHandlers()
    {
        return _container.GetAllInstances<IMissingHandler>();
    }
}