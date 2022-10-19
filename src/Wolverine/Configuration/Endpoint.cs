using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks.Dataflow;
using Baseline;
using Baseline.Dates;
using Baseline.ImTools;
using Oakton.Descriptions;
using Wolverine.ErrorHandling;
using Wolverine.Runtime;
using Wolverine.Runtime.Routing;
using Wolverine.Runtime.Serialization;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

#nullable enable

namespace Wolverine.Configuration;

/// <summary>
/// Defines how message listening or sending functions
/// at runtime
/// </summary>
public enum EndpointMode
{
    /// <summary>
    /// Persistence backed inbox for listeners or outbox for sending endpoints
    /// </summary>
    Durable,
    
    /// <summary>
    /// Outgoing or incoming messages are buffered in local, in memory queues
    /// </summary>
    BufferedInMemory,
    
    /// <summary>
    /// Incoming messages are processed inline with the external message listening. Outgoing messages are delivered inline
    /// with the triggering operation
    /// </summary>
    Inline
}

public enum EndpointRole
{
    /// <summary>
    /// This endpoint is configured by Wolverine itself
    /// </summary>
    System,
    
    /// <summary>
    /// This endpoint is configured and owned by the application itself
    /// </summary>
    Application
}

/// <summary>
///     Configuration for a single message listener within a Wolverine application
/// </summary>
public abstract class Endpoint :  ICircuitParameters, IDescribesProperties
{
    private IMessageSerializer? _defaultSerializer;
    private string? _name;
    private ImHashMap<string, IMessageSerializer> _serializers = ImHashMap<string, IMessageSerializer>.Empty;
    internal readonly List<IDelayedEndpointConfiguration> DelayedConfiguration = new();

    protected Endpoint(Uri uri, EndpointRole role)
    {
        Role = role;
        Uri = uri;
    }
    
    /// <summary>
    /// Is the endpoint controlled and configured by the application or Wolverine itself?
    /// </summary>
    public EndpointRole Role { get; }

    internal void RegisterDelayedConfiguration(IDelayedEndpointConfiguration configuration)
    {
        DelayedConfiguration.Add(configuration);
    }

    private bool _hasCompiled;
    
    internal void Compile(IWolverineRuntime runtime)
    {
        if (_hasCompiled) return;
        
        foreach (var policy in runtime.Options.Transports.EndpointPolicies)
        {
            policy.Apply(this, runtime);
        }

        foreach (var configuration in DelayedConfiguration.ToArray())
        {
            configuration.Apply();
        }

        DefaultSerializer ??= runtime.Options.DefaultSerializer;

        _hasCompiled = true;
    }

    private EndpointMode _mode = EndpointMode.BufferedInMemory;

    /// <summary>
    /// Local message buffering limits and restart thresholds for back pressure mechanics
    /// </summary>
    public BufferingLimits BufferingLimits { get; set; } = new(1000, 500);
    
    /// <summary>
    /// If present, adds a circuit breaker to the active listening agent
    /// for this endpoint at runtime
    /// </summary>
    public CircuitBreakerOptions? CircuitBreakerOptions { get; set; }

    internal IList<Subscription> Subscriptions { get; } = new List<Subscription>();

    /// <summary>
    ///     Mark whether or not the receiver for this listener should use
    ///     message persistence for durability
    /// </summary>
    public EndpointMode Mode
    {
        get => _mode;
        set
        {
            if (!supportsMode(value))
            {
                throw new InvalidOperationException(
                    $"Endpoint of type {GetType().Name} does not support EndpointMode.{value}");
            }

            _mode = value;
        }
    }

    
    public RoutingMode RoutingType { get; set; } = RoutingMode.Static;

    internal bool ShouldSendMessage(Type messageType)
    {
        return Subscriptions.Any(x => x.Matches(messageType));
    }


    protected virtual bool supportsMode(EndpointMode mode)
    {
        return true;
    }


    public virtual bool AutoStartSendingAgent() => Subscriptions.Any();


    internal IWolverineRuntime? Runtime { get; set; }

    /// <summary>
    /// Get or override the default message serializer for just this endpoint
    /// </summary>
    /// <exception cref="ArgumentNullException"></exception>
    public IMessageSerializer? DefaultSerializer
    {
        get => _defaultSerializer;
        set
        {
            RegisterSerializer(value ?? throw new ArgumentNullException(nameof(value)));
            _defaultSerializer = value;
        }
    }


    /// <summary>
    ///     Descriptive Name for this listener. Optional.
    /// </summary>
    public string EndpointName
    {
        get => _name ?? Uri.ToString();
        set => _name = value;
    }

    /// <summary>
    ///     The actual address of the listener, including the transport scheme
    /// </summary>
    public Uri Uri { get; }

    /// <summary>
    /// Configuration for the local TPL Dataflow queue for listening endpoints configured as either
    /// BufferedInMemory or Durable
    /// </summary>
    public ExecutionDataflowBlockOptions ExecutionOptions { get; set; } = new();

    /// <summary>
    /// Is this endpoint used to listen for incoming messages?
    /// </summary>
    public bool IsListener { get; set; }

    /// <summary>
    /// Is this a preferred endpoint for replies to the system?
    /// </summary>
    public bool IsUsedForReplies { get; set; }

    internal IList<IEnvelopeRule> OutgoingRules { get; } = new List<IEnvelopeRule>();

    internal ISendingAgent? Agent { get; set; }


    /// <summary>
    ///     Duration of time to wait before attempting to "ping" a transport
    ///     in an attempt to resume a broken sending circuit
    /// </summary>
    public TimeSpan PingIntervalForCircuitResume { get; set; } = 1.Seconds();

    /// <summary>
    ///     How many times outgoing message sending can fail before tripping
    ///     off the circuit breaker functionality. Applies to all transport types
    /// </summary>
    public int FailuresBeforeCircuitBreaks { get; set; } = 3;

    /// <summary>
    ///     Caps the number of envelopes held in memory for outgoing retries
    ///     if an outgoing transport fails.
    /// </summary>
    public int MaximumEnvelopeRetryStorage { get; set; } = 100;

    /// <summary>
    /// Optional default message type if this endpoint only receives one message type
    /// </summary>
    public Type? MessageType { get; set; }

    public virtual IDictionary<string, object> DescribeProperties()
    {
        var dict = new Dictionary<string, object>
        {
            { nameof(EndpointName), EndpointName },
            { nameof(Mode), Mode },
            { nameof(PingIntervalForCircuitResume), PingIntervalForCircuitResume },
            { nameof(FailuresBeforeCircuitBreaks), PingIntervalForCircuitResume }
        };

        if (Mode == EndpointMode.BufferedInMemory)
        {
            dict.Add(nameof(MaximumEnvelopeRetryStorage), MaximumEnvelopeRetryStorage);

            if (IsListener && Mode != EndpointMode.Inline)
            {
                dict.Add("ExecutionOptions.MaxDegreeOfParallelism", ExecutionOptions.MaxDegreeOfParallelism);
                dict.Add("ExecutionOptions.EnsureOrdered", ExecutionOptions.EnsureOrdered);
                dict.Add("ExecutionOptions.SingleProducerConstrained", ExecutionOptions.SingleProducerConstrained);
                dict.Add("ExecutionOptions.MaxMessagesPerTask", ExecutionOptions.MaxMessagesPerTask);
            }
        }


        return dict;
    }

    internal IMessageSerializer? TryFindSerializer(string? contentType)
    {
        if (contentType.IsEmpty())
        {
            return null;
        }

        if (_serializers.TryFind(contentType, out var serializer))
        {
            return serializer;
        }

        serializer = Runtime?.Options.TryFindSerializer(contentType);
        _serializers = _serializers!.AddOrUpdate(contentType, serializer)!;

        return serializer;
    }

    /// <summary>
    /// Add an additional message serializer to just this endpoint
    /// </summary>
    /// <param name="serializer"></param>
    public void RegisterSerializer(IMessageSerializer serializer)
    {
        _serializers = _serializers.AddOrUpdate(serializer.ContentType, serializer);
    }

    /// <summary>
    /// Build a message listener for this endpoint at runtime
    /// </summary>
    /// <param name="runtime"></param>
    /// <param name="receiver"></param>
    /// <returns></returns>
    public abstract IListener BuildListener(IWolverineRuntime runtime, IReceiver receiver);

    /// <summary>
    /// Create new sending agent for this 
    /// </summary>
    /// <param name="runtime"></param>
    /// <param name="replyUri"></param>
    /// <returns></returns>
    protected internal virtual ISendingAgent StartSending(IWolverineRuntime runtime,
        Uri? replyUri)
    {
        var sender = runtime.Advanced.StubAllOutgoingExternalSenders ? new NullSender(Uri) : CreateSender(runtime);
        return runtime.Endpoints.CreateSendingAgent(replyUri, sender, this);
    }
    
    protected abstract ISender CreateSender(IWolverineRuntime runtime);

    // This is only surviving to support testing
    internal void ApplyEnvelopeRules(Envelope envelope)
    {
        foreach (var rule in OutgoingRules)
        {
            rule.Modify(envelope);
        }
    }

    public virtual bool ShouldEnforceBackPressure()
    {
        return Mode != EndpointMode.Inline;
    }
}
