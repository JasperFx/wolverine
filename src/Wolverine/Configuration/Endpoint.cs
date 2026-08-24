#nullable enable

using System.Text.Json;
using ImTools;
using JasperFx.CommandLine.Descriptions;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Descriptors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wolverine.ErrorHandling;
using Wolverine.Runtime;
using Wolverine.Runtime.Interop;
using Wolverine.Runtime.Routing;
using Wolverine.Runtime.Scheduled;
using Wolverine.Runtime.Serialization;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.Configuration;

public enum PartitionSlots
{
    Three = 3,
    Five = 5,
    Seven = 7,
    Nine = 9
}

/// <summary>
/// Marker interface that tells Wolverine internals that this endpoint directly
/// integrates with the active transactional inbox
/// </summary>
/// <remarks>
/// GH-4035. This is <i>only</i> about inbox integration: it routes scheduled retries to
/// <see cref="ScheduleRetryAsync"/> and it tells <c>DurableReceiver</c> that the endpoint persists
/// incoming messages itself, so the arrival INSERT is skipped and the delivery is completed on receipt.
/// Do not reuse it to mean "this queue has storage of its own" -- see <see cref="IStorageBackedQueue"/>.
/// </remarks>
public interface IDatabaseBackedEndpoint
{
    Task ScheduleRetryAsync(Envelope envelope, CancellationToken cancellation);
}

/// <summary>
/// Marker for a queue whose contents live in storage that Wolverine itself provisions -- the database
/// queue tables, or a Redis stream and its scheduled sorted set -- rather than in an external broker.
/// <see cref="Wolverine.Runtime.StorageExtensions.ClearAllWolverineStorageAsync"/> builds and empties
/// exactly these.
/// </summary>
/// <remarks>
/// GH-4035. This used to be inferred from <see cref="IDatabaseBackedEndpoint"/>, which meant a change
/// to an endpoint's <i>inbox</i> behaviour silently changed whether integration tests could reset it.
/// Removing that marker from the Redis stream endpoint in GH-4028 dropped Redis out of the reset with
/// nothing in CI able to see it. The two concerns are separate now: an endpoint may be either, both, or
/// neither.
/// </remarks>
public interface IStorageBackedQueue;

public enum TenancyBehavior
{
    /// <summary>
    /// In the case of being used within a multi-tenancy aware transport setup,
    /// this endpoint is tenant specific
    /// </summary>
    TenantAware,
    
    /// <summary>
    /// In the case of being used within a multi-tenancy aware transport setup,
    /// this endpoint is global across all tenants
    /// </summary>
    Global
}

/// <summary>
///     Defines how message listening or sending functions
///     at runtime
/// </summary>
public enum EndpointMode
{
    /// <summary>
    ///     Persistence backed inbox for listeners or outbox for sending endpoints
    /// </summary>
    Durable,

    /// <summary>
    ///     Outgoing or incoming messages are buffered in local, in memory queues
    /// </summary>
    BufferedInMemory,

    /// <summary>
    ///     Incoming messages are processed inline with the external message listening. Outgoing messages are delivered inline
    ///     with the triggering operation
    /// </summary>
    Inline,

    /// <summary>
    ///     Incoming messages flow through an in-memory (optionally group-partitioned) execution block while the broker
    ///     delivery is held unacknowledged, and are settled natively -- acked on handler success, nacked or dead-lettered
    ///     on terminal failure -- from the completion continuation. Buffered's throughput and partitioning with Inline's
    ///     no-loss guarantee, and no database involvement. See GH-3708.
    ///     <para>
    ///     Opt-in per transport: a transport must override <c>supportsNativeAck</c> to accept this mode, because most
    ///     settlement models cannot express out-of-order completion.
    ///     </para>
    /// </summary>
    NativeAck
}

public enum EndpointRole
{
    /// <summary>
    ///     This endpoint is configured by Wolverine itself
    /// </summary>
    System,

    /// <summary>
    ///     This endpoint is configured and owned by the application itself
    /// </summary>
    Application
}

public enum ListenerScope
{
    /// <summary>
    /// If this endpoint is a listener, it should be active on all nodes for
    /// competing consumers load balancing
    /// </summary>
    CompetingConsumers,

    /// <summary>
    /// If this endpoint is a listener, it should only be active on a single node.
    /// This is mostly appropriate for
    /// </summary>
    Exclusive, 
    
    /// <summary>
    /// This listening endpoint should only be active on leader nodes (or when running in Solo). This
    /// setting is probably only useful for administrative functions
    /// </summary>
    PinnedToLeader
}

public abstract class Endpoint<TMapper, TConcreteMapper> : Endpoint
    where TConcreteMapper : TMapper, IEnvelopeMapper
{
    protected Endpoint(Uri uri, EndpointRole role) : base(uri, role)
    {
        
    }

    private Action<TConcreteMapper, IWolverineRuntime>? _customizeMapping;

    protected internal void customizeMapping(Action<TConcreteMapper, IWolverineRuntime> customization)
    {
        _customizeMapping = customization ?? throw new ArgumentNullException(nameof(customization));
    }

    protected internal void registerMapperFactory(Func<IWolverineRuntime, TMapper> factory)
    {
        _mapperFactory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    private Func<IWolverineRuntime, TMapper>? _mapperFactory;
    
    public TMapper BuildMapper(IWolverineRuntime runtime)
    {
       if (EnvelopeMapper != null) return EnvelopeMapper;

       if (_mapperFactory != null)
       {
           return _mapperFactory(runtime);
       }
       
       var mapper = buildMapper(runtime);
       _customizeMapping?.Invoke(mapper, runtime);
       
       if (MessageType != null)
       {
           mapper.ReceivesMessage(MessageType);
       }

       return mapper;
    }

    protected abstract TConcreteMapper buildMapper(IWolverineRuntime runtime);


    /// <summary>
    /// When set, overrides the built in envelope mapping with a custom
    /// implementation
    /// </summary>
    public TMapper? EnvelopeMapper { get; set; }

    /// <summary>
    /// True when the user has explicitly wired a custom mapper instance or factory in
    /// place of the per-transport default <see cref="buildMapper"/>. Drives the
    /// <c>"Custom"</c> value on <see cref="Capabilities.EndpointDescriptor.InteropMode"/>
    /// so monitoring tools (e.g. CritterWatch) can flag endpoints that are using a
    /// non-default envelope shape. See #2641.
    /// </summary>
    protected internal override bool HasCustomEnvelopeMapper =>
        EnvelopeMapper is not null || _mapperFactory is not null;
}

/// <summary>
///     Configuration for a single message listener within a Wolverine application
/// </summary>
public abstract class Endpoint : ICircuitParameters, IDescribesProperties
{
    internal readonly List<IDelayedEndpointConfiguration> DelayedConfiguration = new();
    private IMessageSerializer? _defaultSerializer;

    private bool _hasCompiled;
    private int _maxDegreeOfParallelism = Math.Max(Environment.ProcessorCount, 5);
    private BufferingLimits _bufferingLimits = new(1000, 500);

    private EndpointMode _mode = EndpointMode.BufferedInMemory;
    private string? _name;
    private ImHashMap<string, IMessageSerializer> _serializers = ImHashMap<string, IMessageSerializer>.Empty;

    internal ImHashMap<Type, MessageRoute> Routes = ImHashMap<Type, MessageRoute>.Empty;

    protected Endpoint(Uri uri, EndpointRole role)
    {
        Role = role;
        Uri = uri;
        EndpointName = uri.ToString();
    }

    /// <summary>
    /// Short, human-readable name of the underlying broker object kind this endpoint
    /// represents — e.g. <c>"queue"</c>, <c>"exchange"</c>, <c>"topic"</c>,
    /// <c>"subscription"</c>, <c>"stream"</c>. Each transport-specific subclass sets
    /// this value in its constructor; transports whose role is only knowable at
    /// runtime (e.g. <c>NatsEndpoint</c> choosing between Core <c>subject</c> and
    /// JetStream <c>stream</c>) override the property. Surfaced to CritterWatch and
    /// other diagnostic UIs to drive endpoint display. See GH-2601.
    /// </summary>
    public virtual string BrokerRole { get; protected set; } = "endpoint";

    /// <summary>
    /// Controls the maximum number of messages that could be processed at one time.
    /// Default is the greater of Environment.ProcessorCount or 5. Setting this to 1 makes this listening endpoint
    /// be ordered in its processing.
    ///
    /// Only applies to <see cref="EndpointMode.BufferedInMemory"/> and <see cref="EndpointMode.Durable"/>
    /// endpoints, because it governs the size of Wolverine's local execution block -- and an
    /// <see cref="EndpointMode.Inline"/> endpoint has no execution block at all. An Inline endpoint's
    /// concurrency is whatever the transport listener itself does; this value is normalized to 1 at
    /// <see cref="Compile"/> time for an Inline endpoint. See GH-3712.
    /// </summary>
    public int MaxDegreeOfParallelism
    {
        get => _maxDegreeOfParallelism;
        set
        {
            _maxDegreeOfParallelism = value;

            // GH-3712. Distinguishes "the user asked for parallelism" from "nobody ever touched this",
            // so the Inline coherence check can warn about the former without nagging about the default.
            MaxDegreeOfParallelismIsExplicit = true;
        }
    }

    /// <summary>
    /// GH-3712. Has anything actually assigned <see cref="MaxDegreeOfParallelism"/>, as opposed to
    /// leaving the Environment.ProcessorCount-derived default in place?
    /// </summary>
    internal bool MaxDegreeOfParallelismIsExplicit { get; private set; }

    /// <summary>
    /// GH-3712. The explicitly configured <see cref="MaxDegreeOfParallelism"/> that <see cref="Compile"/>
    /// discarded because this endpoint's mode ignores it. Null when nothing was discarded.
    /// </summary>
    internal int? DiscardedMaxDegreeOfParallelism { get; private set; }

    /// <summary>
    /// GH-3712. Does this endpoint's mode ignore <see cref="MaxDegreeOfParallelism"/> outright? Used by the
    /// diagnostics output so an ignored setting is never displayed as though it were live.
    /// </summary>
    internal bool ModeIgnoresParallelism => Mode == EndpointMode.Inline;

    /// <summary>
    /// GH-3712. Render <see cref="MaxDegreeOfParallelism"/> for diagnostics, saying "n/a" rather than
    /// printing a dead number for a mode that never reads it.
    /// </summary>
    internal string DescribeMaxDegreeOfParallelism()
    {
        return ModeIgnoresParallelism ? $"n/a ({Mode})" : MaxDegreeOfParallelism.ToString();
    }

    /// <summary>
    /// If specified, directs this endpoint to use by GroupId sharding in processing.
    /// Only impacts Buffered or Durable endpoints though -- an Inline endpoint has no execution block
    /// to shard, so Wolverine rejects that combination at bootstrap rather than silently dropping the
    /// grouping semantics. See GH-3712.
    /// </summary>
    public PartitionSlots? GroupShardingSlotNumber { get; set; }

    /// <summary>
    /// In the case of using "sticky handlers"
    /// </summary>
    [IgnoreDescription]
    public List<Type> StickyHandlers { get; } = new();

    /// <summary>
    /// Governs whether this endpoint should be "per tenant" or global in the case of using
    /// a broker per tenant
    /// </summary>
    public TenancyBehavior TenancyBehavior { get; set; } = TenancyBehavior.TenantAware;

    /// <summary>
    /// If a listener, what is the scope of the
    /// </summary>
    public ListenerScope ListenerScope { get; set; } = ListenerScope.CompetingConsumers;

    /// <summary>
    /// GH-3590. Is this endpoint's listener only ever active on ONE node of the cluster? Inbox recovery for
    /// such an endpoint is owned by the node hosting the listener (<see cref="ListenerInboxRecovery"/>) rather
    /// than by the per-database durability agent, which is assigned per database and routinely lands on a
    /// different node. Every guard that implements that hand-off asks *this* question, so that the two sides
    /// can never disagree and strand messages in between.
    /// </summary>
    internal virtual bool IsSingleNodeListener => ListenerScope != ListenerScope.CompetingConsumers;

    /// <summary>
    /// Is OpenTelemetry enabled for this endpoint?
    /// </summary>
    public bool TelemetryEnabled { get; set; } = true;

    /// <summary>
    /// When using <see cref="EndpointMode.Inline"/>, setting this to <c>true</c> will allow
    /// already-ingested messages to continue processing while the receiver is draining, only
    /// deferring messages after the drain has fully completed. When <c>false</c> (the default),
    /// messages are deferred as soon as the drain begins.
    /// </summary>
    public bool ProcessInlineWhileDraining { get; set; }

    /// <summary>
    ///     Is the endpoint controlled and configured by the application or Wolverine itself?
    /// </summary>
    public EndpointRole Role { get; internal set; }

    /// <summary>
    ///     Local message buffering limits and restart thresholds for back pressure mechanics.
    ///     Inert on an <see cref="EndpointMode.Inline"/> endpoint, which never builds a
    ///     <c>BackPressureAgent</c> -- see <see cref="ShouldEnforceBackPressure"/> and GH-3712.
    /// </summary>
    [ChildDescription]
    public BufferingLimits BufferingLimits
    {
        get => _bufferingLimits;
        set
        {
            _bufferingLimits = value;

            // GH-3712, same rationale as MaxDegreeOfParallelismIsExplicit
            BufferingLimitsAreExplicit = true;
        }
    }

    /// <summary>
    /// GH-3712. Has anything actually assigned <see cref="BufferingLimits"/>?
    /// </summary>
    internal bool BufferingLimitsAreExplicit { get; private set; }

    /// <summary>
    ///     If present, adds a circuit breaker to the active listening agent
    ///     for this endpoint at runtime
    /// </summary>
    [ChildDescription]
    public CircuitBreakerOptions? CircuitBreakerOptions { get; set; }

    public IList<Subscription> Subscriptions { get; } = new List<Subscription>();


    /// <summary>
    /// For endpoints that send or receive messages in batches, this governs the maximum
    /// number of messages that will be received or sent in one batch. Defaults to 100.
    /// </summary>
    public int MessageBatchSize { get; set; } = 100;

    /// <summary>
    /// For endpoints that send messages in batches, this governs the maximum number
    /// of concurrent outgoing batches
    /// </summary>
    public int MessageBatchMaxDegreeOfParallelism { get; set; } = 1;

    /// <summary>
    /// For endpoints that send messages in batches, this is the maximum time the
    /// sender will wait to accumulate a full batch before flushing what it has.
    /// Defaults to 250ms.
    /// </summary>
    public TimeSpan MessageBatchTimeout { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>
    ///     Mark whether or not the receiver for this listener should use
    ///     message persistence for durability
    /// </summary>
    public EndpointMode Mode
    {
        get => _mode;
        set
        {
            // GH-3708. NativeAck is gated on its OWN predicate rather than through supportsMode(), deliberately.
            // supportsMode() is default-open -- the base returns true, and several overrides are written as
            // negations (TcpEndpoint's "mode != Inline", SignalRTransport's "mode != Durable") or as a blanket
            // true (HttpEndpoint) -- so routing this member through it would have every un-audited transport
            // silently accept a mode whose settlement model it cannot express. A separate default-false predicate
            // cannot be leaked by an existing override, so only a transport that opts in explicitly accepts it.
            if (value == EndpointMode.NativeAck && !supportsNativeAck)
            {
                throw new InvalidOperationException(
                    $"Endpoint of type {GetType().Name} does not support EndpointMode.{nameof(EndpointMode.NativeAck)}. " +
                    "Native ack processing requires a transport that settles each delivery individually and can settle " +
                    "deliveries out of order; the transport opts in by overriding supportsNativeAck.");
            }

            if (!supportsMode(value))
            {
                throw new InvalidOperationException(
                    $"Endpoint of type {GetType().Name} does not support EndpointMode.{value}");
            }

            _mode = value;
        }
    }

    public RoutingMode RoutingType { get; set; } = RoutingMode.Static;


    internal IWolverineRuntime? Runtime { get; set; }

    /// <summary>
    /// When true, this endpoint will resolve an <see cref="IWireTap"/> from the IoC container
    /// to record message success/failure for auditing purposes.
    /// </summary>
    internal bool UseWireTap { get; set; }

    /// <summary>
    /// Optional keyed service key for resolving a specific <see cref="IWireTap"/>
    /// implementation from the IoC container. When null, the default (non-keyed)
    /// IWireTap registration is used.
    /// </summary>
    internal string? WireTapServiceKey { get; set; }

    /// <summary>
    /// The resolved wire tap instance, populated during <see cref="Compile"/>.
    /// </summary>
    [IgnoreDescription]
    internal IWireTap? WireTap { get; set; }

    /// <summary>
    /// Used by <see cref="Capabilities.EndpointDescriptor"/> to surface a <c>"Custom"</c>
    /// interop mode when the user has wired a non-default envelope mapper for this
    /// endpoint. The base implementation returns <c>false</c> so non-typed endpoints
    /// (local queues, the database control transports, TCP, etc.) are reported as
    /// using the framework default. The generic <see cref="Endpoint{TMapper, TConcreteMapper}"/>
    /// overrides this. See #2641.
    /// </summary>
    [IgnoreDescription]
    protected internal virtual bool HasCustomEnvelopeMapper => false;

    /// <summary>
    ///     Get or override the default message serializer for just this endpoint
    /// </summary>
    /// <exception cref="ArgumentNullException"></exception>
    [IgnoreDescription]
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
    ///     Is this endpoint used to listen for incoming messages?
    /// </summary>
    public bool IsListener { get; set; } // TODO -- in 3.0, switch this to using ListeningScope

    /// <summary>
    ///     Is this a preferred endpoint for replies to the system?
    /// </summary>
    public bool IsUsedForReplies { get; set; }

    [IgnoreDescription]
    public IList<IEnvelopeRule> OutgoingRules { get; } = new List<IEnvelopeRule>();
    
    [IgnoreDescription]
    public IList<IEnvelopeRule> IncomingRules { get; } = new List<IEnvelopeRule>();

    /// <summary>
    /// In some cases, you may want to tell Wolverine that any message
    /// coming into this endpoint are automatically tagged to a certain
    /// tenant id
    /// </summary>
    public virtual string? TenantId { get; set; }

    /// <summary>
    ///     The name of the external system on the other end of this endpoint — "Stripe", "Legacy ERP" —
    ///     when a listener receives from, or a sender publishes to, something outside this application.
    ///     Optional and purely descriptive (GH-3989): the <em>edge</em> of an Event Modeling translation
    ///     slice is derived from the endpoint itself, but the name is the one thing code cannot say, so it
    ///     is declared here and flows out through <c>EndpointDescriptor.ExternalSystem</c> and onto the
    ///     slice as an external-system element. Set with <c>.ExternalSystem("Stripe")</c> on the listener
    ///     or subscriber configuration.
    /// </summary>
    public string? ExternalSystemName { get; set; }
    
    internal IEnumerable<IEnvelopeRule> RulesForIncoming()
    {
        foreach (var rule in IncomingRules)
        {
            yield return rule;
        }

        if (MessageType != null)
        {
            yield return new MessageTypeRule(MessageType);
        }

        if (TenantId.IsNotEmpty())
        {
            yield return new TenantIdRule(TenantId);
        }
    }

    internal ISendingAgent? Agent { get; set; }

    /// <summary>
    ///     Optional default message type if this endpoint only receives one message type
    /// </summary>
    public Type? MessageType { get; set; }

    /// <summary>
    ///     Number of parallel listeners for this endpoint
    /// </summary>
    public int ListenerCount { get; set; } = 1;


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
    /// Per-endpoint failure handling policies for outgoing message send failures.
    /// When set, these rules take priority over the global SendingFailure policies.
    /// </summary>
    public SendingFailurePolicies? SendingFailure { get; set; }

    public virtual IDictionary<string, object> DescribeProperties()
    {
        var dict = new Dictionary<string, object>
        {
            { nameof(EndpointName), EndpointName },
            { nameof(Mode), Mode },
            { nameof(PingIntervalForCircuitResume), PingIntervalForCircuitResume },
            { nameof(FailuresBeforeCircuitBreaks), FailuresBeforeCircuitBreaks }
        };

        if (Mode == EndpointMode.BufferedInMemory)
        {
            dict.Add(nameof(MaximumEnvelopeRetryStorage), MaximumEnvelopeRetryStorage);
        }

        if (ShouldEnforceBackPressure())
        {
            dict.Add($"{nameof(BufferingLimits)}.{nameof(BufferingLimits.Maximum)}", BufferingLimits.Maximum);
            dict.Add($"{nameof(BufferingLimits)}.{nameof(BufferingLimits.Restart)}", BufferingLimits.Restart);
        }

        if (CircuitBreakerOptions != null)
        {
            dict.Add($"{nameof(CircuitBreakerOptions)}.{nameof(CircuitBreakerOptions.FailurePercentageThreshold)}",
                CircuitBreakerOptions.FailurePercentageThreshold);
            dict.Add($"{nameof(CircuitBreakerOptions)}.{nameof(CircuitBreakerOptions.PauseTime)}",
                CircuitBreakerOptions.PauseTime);
        }

        return dict;
    }

    internal MessageRoute RouteFor(Type messageType, IWolverineRuntime runtime)
    {
        if (Routes.TryFind(messageType, out var route))
        {
            return route;
        }

        route = MessageRoute.For(messageType, this, runtime);

        Routes = Routes.AddOrUpdate(messageType, route);

        return route;
    }
    


    internal void RegisterDelayedConfiguration(IDelayedEndpointConfiguration configuration)
    {
        DelayedConfiguration.Add(configuration);
    }

    public void Compile(IWolverineRuntime runtime)
    {
        if (_hasCompiled)
        {
            return;
        }

        Runtime = runtime;

        foreach (var policy in runtime.Options.Transports.EndpointPolicies) policy.Apply(this, runtime);

        foreach (var configuration in DelayedConfiguration.ToArray()) configuration.Apply();

        DefaultSerializer ??= runtime.Options.DefaultSerializer;

        if (UseWireTap)
        {
            WireTap = ResolveWireTap(runtime);
        }

        // Pre-populate the endpoint-local serializer cache with every globally-
        // registered serializer (keyed by content-type). This eliminates the
        // first-miss hot-path mutation in TryFindSerializer (formerly an
        // ImHashMap.AddOrUpdate on every previously-unseen content-type), making
        // steady-state lookups pure reads. Endpoint-level overrides registered
        // via RegisterSerializer prior to Compile() are preserved — they take
        // precedence because TryAdd skips entries already in the map.
        foreach (var pair in runtime.Options.ToSerializerDictionary())
        {
            if (!_serializers.Contains(pair.Key))
            {
                _serializers = _serializers.AddOrUpdate(pair.Key, pair.Value);
            }
        }

        normalizeForMode();

        _hasCompiled = true;
    }

    /// <summary>
    /// GH-3712. Converge the endpoint on one state per mode regardless of the order the fluent calls were
    /// made in. <c>ProcessInline()</c> used to clamp MaxDegreeOfParallelism eagerly, which meant
    /// <c>.MaximumParallelMessages(20).ProcessInline()</c> and <c>.ProcessInline().MaximumParallelMessages(20)</c>
    /// ended with different endpoint state for the same two calls. Doing it here -- after endpoint policies and
    /// all delayed configuration have run -- makes the final mode, not the call sequence, decide.
    /// </summary>
    private void normalizeForMode()
    {
        if (Mode != EndpointMode.Inline) return;

        if (MaxDegreeOfParallelismIsExplicit && _maxDegreeOfParallelism > 1)
        {
            DiscardedMaxDegreeOfParallelism = _maxDegreeOfParallelism;
        }

        // Assign the backing field directly so the "was explicitly set" flag still reflects
        // the *user's* intent rather than this normalization.
        _maxDegreeOfParallelism = 1;
    }

    private IWireTap? ResolveWireTap(IWolverineRuntime runtime)
    {
        var services = runtime.Services;
        if (WireTapServiceKey != null)
        {
            return services.GetKeyedService<IWireTap>(WireTapServiceKey);
        }

        return services.GetService<IWireTap>();
    }

    internal bool ShouldSendMessage(Type messageType)
    {
        // Subscriptions added by an IMessageRoutingConvention's PreregisterSenders pass
        // (GH-2588) are NOT explicit publish rules — they exist solely so endpoint
        // policies like UseDurableOutboxOnAllSendingEndpoints can see Subscriptions.Any()
        // at Compile() time. ExplicitRouting and the diagnostics command both call this
        // method to identify user-wired publish rules; counting conventional subscriptions
        // here would short-circuit past LocalRouting / MessageRoutingConventions and break
        // routing precedence for handled messages.
        return Subscriptions.Any(x => !x.IsFromConvention && x.Matches(messageType));
    }

    protected virtual bool supportsMode(EndpointMode mode)
    {
        return true;
    }

    /// <summary>
    /// GH-3708. Does this endpoint's transport accept <see cref="EndpointMode.NativeAck"/>? Default is <c>false</c> for
    /// every endpoint type -- unlike <see cref="supportsMode"/>, which is default-open. A transport may only answer true
    /// if it settles each delivery individually AND tolerates settling them out of order, because the partitioned
    /// execution block completes messages in handler-completion order rather than delivery order. Kafka, for instance,
    /// cannot: a cumulative offset commit has no way to express a gap.
    /// </summary>
    protected virtual bool supportsNativeAck => false;

    /// <summary>
    /// GH-4048. Does this endpoint's broker put a clock on an <em>unsettled</em> delivery? True for SQS (visibility
    /// timeout), Azure Service Bus (lock duration), Pub/Sub (ack deadline) and JetStream (AckWait); false for
    /// RabbitMQ and Redis Streams, where an unacked delivery lives until the channel closes and there is nothing
    /// to renew.
    /// </summary>
    /// <remarks>
    /// This is the endpoint-side half of the lease contract. Under <see cref="EndpointMode.NativeAck"/> a delivery
    /// is held unsettled for lane queue time <em>plus</em> handler time, so an endpoint that answers true here must
    /// build a listener implementing <see cref="Transports.ISupportLeaseRenewal"/>. <c>ListeningAgent</c> enforces
    /// that at startup, deliberately: without the check, opting into NativeAck and forgetting renewal produces a
    /// silent duplicate generator instead of an error.
    /// </remarks>
    protected internal virtual bool holdsExpiringLease => false;

    /// <summary>
    /// Check if this endpoint supports the specified mode
    /// </summary>
    public bool SupportsMode(EndpointMode mode)
    {
        return mode == EndpointMode.NativeAck ? supportsNativeAck : supportsMode(mode);
    }

    // Is this endpoint part of a sharded messaging topology?
    // If so, this should be "auto-started"
    internal bool UsedInShardedTopology { get; set; }

    /// <summary>
    /// If set, this endpoint is part of a global partitioned topology and messages
    /// should be forwarded to this companion local queue URI for processing.
    /// </summary>
    internal Uri? GlobalPartitionLocalQueueUri { get; set; }

    /// <summary>
    /// GH-3867. Set when a <c>BatchMessagesOf</c> definition executes its assembled batches on this
    /// endpoint. Such a queue is its own cascade target: the messages it executes are absorbed into
    /// a batching channel and the resulting batch is enqueued right back onto it. With a bounded
    /// execution block that closes a cycle through the batching channel's own bounded buffers and
    /// can wedge, so these endpoints get an unbounded one — the same trade GH-3287 made for local
    /// queues. Back-pressure is preserved by <see cref="Runtime.Batching.BatchingPendingCounts" />,
    /// which counts pending members against the originating external listener.
    /// </summary>
    internal bool HostsBatchExecution { get; set; }

    public virtual bool AutoStartSendingAgent()
    {
        return UsedInShardedTopology || Subscriptions.Any();
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

        // Compile() pre-seeds _serializers with every globally-registered content-
        // type, so reaching this fallback means the message arrived with a content-
        // type that wasn't registered at bootstrap. Read from the global registry
        // without mutating the endpoint cache — under sustained traffic with an
        // unregistered content-type that would otherwise be a hot-path write on
        // every call, but in practice this branch fires rarely (and the global
        // lookup is itself O(1) against a small dictionary).
        return Runtime?.Options.TryFindSerializer(contentType);
    }

    /// <summary>
    ///     Add an additional message serializer to just this endpoint
    /// </summary>
    /// <param name="serializer"></param>
    public void RegisterSerializer(IMessageSerializer serializer)
    {
        _serializers = _serializers.AddOrUpdate(serializer.ContentType, serializer);
    }

    /// <summary>
    ///     Build a message listener for this endpoint at runtime
    /// </summary>
    /// <param name="runtime"></param>
    /// <param name="receiver"></param>
    /// <returns></returns>
    public abstract ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver);

    internal IReceiver MaybeWrapReceiver(IReceiver inner)
    {
        var rules = RulesForIncoming().ToArray();
        return rules.Any() ? new ReceiverWithRules(inner, rules) : inner;
    }
    
    /// <summary>
    ///     Create new sending agent for this
    /// </summary>
    /// <param name="runtime"></param>
    /// <param name="replyUri"></param>
    /// <returns></returns>
    protected internal virtual ISendingAgent StartSending(IWolverineRuntime runtime,
        Uri? replyUri)
    {
        // Compile must be called before CreateSender so that delayed configuration
        // (like InteropWithCloudEvents) is applied before the sender calls BuildMapper()
        Compile(runtime);
        var sender = runtime.Options.ExternalTransportsAreStubbed ? new NullSender(Uri) : CreateSender(runtime);
        return runtime.Endpoints.CreateSendingAgent(replyUri, sender, this);
    }

    protected abstract ISender CreateSender(IWolverineRuntime runtime);

    // This is only surviving to support testing
    internal void ApplyEnvelopeRules(Envelope envelope)
    {
        foreach (var rule in OutgoingRules) rule.Modify(envelope);
    }

    public virtual bool ShouldEnforceBackPressure()
    {
        // GH-3708: a NativeAck endpoint is bounded by the broker's prefetch window -- it never acks on receipt, so
        // the broker stops delivering -- which makes an in-process BackPressureAgent redundant, same as Inline.
        return Mode is not (EndpointMode.Inline or EndpointMode.NativeAck);
    }

    /// <summary>
    ///     One time initialization of this endpoint
    /// </summary>
    /// <param name="logger"></param>
    /// <returns></returns>
    public virtual ValueTask InitializeAsync(ILogger logger)
    {
        return ValueTask.CompletedTask;
    }

    internal string SerializerDescription(WolverineOptions options)
    {
        var dict = options.ToSerializerDictionary();
        var overrides = _serializers.Enumerate().Select(x => x.Value)
            .Where(x => !(x is EnvelopeReaderWriter));

        foreach (var serializer in overrides) dict[serializer.ContentType] = serializer;

        dict.Remove("binary/envelope");

        return dict.Select(x => $"{x.Value.GetType().ShortNameInCode()}").Join(", ");
    }

    public virtual bool TryBuildDeadLetterSender(IWolverineRuntime runtime, out ISender? deadLetterSender)
    {
        deadLetterSender = default;
        return false;
    }

    /// <summary>
    /// A transport-agnostic declaration of where this endpoint's dead letters effectively go —
    /// Wolverine's durable store (<see cref="DeadLetterStorageMode.Durable"/>), a native broker dead
    /// letter queue (<see cref="DeadLetterStorageMode.Native"/>), or a native queue bridged back into
    /// durable storage (<see cref="DeadLetterStorageMode.NativeWithRecovery"/>). Monitoring tools read
    /// this through <see cref="Capabilities.EndpointDescriptor.DeadLetterStorage"/> to detect
    /// endpoints whose dead letters are native and un-bridged. The default is
    /// <see cref="DeadLetterStorageMode.Durable"/>; transports with a native dead letter queue
    /// override this.
    /// </summary>
    public virtual DeadLetterStorageMode DeadLetterStorage => DeadLetterStorageMode.Durable;

    internal bool ShouldAutoStartAsListener(DurabilitySettings durability)
    {
        if (!IsListener) return false;
        switch (durability.Mode)
        {
            case DurabilityMode.Solo:
                return true;

            case DurabilityMode.Balanced:
                return ListenerScope == ListenerScope.CompetingConsumers;

            case DurabilityMode.MediatorOnly:
            case DurabilityMode.Serverless:
                return false;
        }

        return true;
    }

    public CloudEventsMapper BuildCloudEventsMapper(IWolverineRuntime runtime, JsonSerializerOptions options)
    {
        return new CloudEventsMapper(runtime.Options.HandlerGraph, options);
    }
}