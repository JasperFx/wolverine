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

public enum EndpointMode
{
    Durable,
    BufferedInMemory,
    Inline
}

/// <summary>
///     Configuration for a single message listener within a Wolverine application
/// </summary>
public abstract class Endpoint :  ICircuitParameters, IDescribesProperties
{
    private IMessageSerializer? _defaultSerializer;
    private string? _name;
    private ImHashMap<string, IMessageSerializer> _serializers = ImHashMap<string, IMessageSerializer>.Empty;

    protected Endpoint()
    {
    }

    protected Endpoint(Uri uri)
    {
        // ReSharper disable once VirtualMemberCallInConstructor
        Parse(uri);
    }

    private EndpointMode _mode = EndpointMode.BufferedInMemory;

    /// <summary>
    /// If present, adds a circuit breaker to the active listening agent
    /// for this endpoint at runtime
    /// </summary>
    public CircuitBreakerOptions? CircuitBreakerOptions { get; set; }

    public IList<Subscription> Subscriptions { get; } = new List<Subscription>();

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

    public IMessageSerializer? DefaultSerializer
    {
        get
        {
            if (_defaultSerializer == null)
            {
                var parent = Runtime?.Options.DefaultSerializer;
                if (parent != null)
                {
                    // Gives you a chance to use per-endpoint JSON settings for example
                    _defaultSerializer = TryFindSerializer(parent.ContentType);
                }
            }

            return _defaultSerializer ??= Runtime?.Options.DefaultSerializer;
        }
        set
        {
            RegisterSerializer(value);
            _defaultSerializer = value;
        }
    }


    /// <summary>
    ///     Descriptive Name for this listener. Optional.
    /// </summary>
    public string Name
    {
        get => _name ?? Uri.ToString();
        set => _name = value;
    }

    /// <summary>
    ///     The actual address of the listener, including the transport scheme
    /// </summary>
    public abstract Uri Uri { get; }

    public ExecutionDataflowBlockOptions ExecutionOptions { get; set; } = new();

    public bool IsListener { get; set; }

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

    public virtual IDictionary<string, object> DescribeProperties()
    {
        var dict = new Dictionary<string, object>
        {
            { nameof(Name), Name },
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

    public void RegisterSerializer(IMessageSerializer serializer)
    {
        _serializers = _serializers.AddOrUpdate(serializer.ContentType, serializer);
    }

    public abstract void Parse(Uri uri);

    public abstract IListener BuildListener(IWolverineRuntime runtime, IReceiver receiver);

    protected virtual internal ISendingAgent StartSending(IWolverineRuntime runtime,
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


}
