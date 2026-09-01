using Azure.Messaging.ServiceBus;
using JasperFx.Core;
using JasperFx.Descriptors;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.AzureServiceBus.Internal;

public interface IAzureServiceBusListeningEndpoint
{
    /// <summary>
    ///     The maximum number of messages to receive in a single batch when listening
    ///     in either buffered or durable modes. The default is 20.
    /// </summary>
    public int MaximumMessagesToReceive { get; set; }

    /// <summary>
    ///     The duration for which the listener waits for a message to arrive in the
    ///     queue before returning. If a message is available, the call returns sooner than this time.
    ///     If no messages are available and the wait time expires, the call returns successfully
    ///     with an empty list of messages. Default is 5 seconds.
    /// </summary>
    public TimeSpan MaximumWaitTime { get; set; }

    /// <summary>
    ///     The number of messages that the underlying Azure Service Bus receiver eagerly buffers
    ///     on the client ahead of any ReceiveMessagesAsync() calls. The default is 0 (prefetch is
    ///     disabled). Be aware that prefetched messages age against the queue's message lock
    ///     duration while they sit in the client buffer, so an oversized prefetch combined with
    ///     slow handlers leads to lock-lost redeliveries.
    /// </summary>
    public int PrefetchCount { get; set; }
}

public abstract class AzureServiceBusEndpoint : Endpoint<IAzureServiceBusEnvelopeMapper, AzureServiceBusEnvelopeMapper>, IBrokerEndpoint, IAzureServiceBusListeningEndpoint
{
    private int? _prefetchCount;

    public AzureServiceBusEndpoint(AzureServiceBusTransport parent, Uri uri, EndpointRole role) : base(uri, role)
    {
        Parent = parent;
    }

    [IgnoreDescription]
    public AzureServiceBusTransport Parent { get; }

    /// <summary>
    ///     The maximum number of messages to receive in a single batch when listening
    ///     in either buffered or durable modes. The default is 20.
    /// </summary>
    public int MaximumMessagesToReceive { get; set; } = 20;

    /// <summary>
    ///     The duration for which the listener waits for a message to arrive in the
    ///     queue before returning. If a message is available, the call returns sooner than this time.
    ///     If no messages are available and the wait time expires, the call returns successfully
    ///     with an empty list of messages. Default is 5 seconds.
    /// </summary>
    public TimeSpan MaximumWaitTime { get; set; } = 5.Seconds();

    /// <summary>
    ///     The number of messages that the underlying Azure Service Bus receiver eagerly buffers
    ///     on the client ahead of any ReceiveMessagesAsync() calls. Falls back to the transport-wide
    ///     default (see AzureServiceBusTransport.PrefetchCount) unless explicitly set on this
    ///     endpoint. The ultimate default is 0 (prefetch is disabled). Be aware that prefetched
    ///     messages age against the queue's message lock duration while they sit in the client
    ///     buffer, so an oversized prefetch combined with slow handlers leads to lock-lost
    ///     redeliveries.
    /// </summary>
    /// <summary>
    /// GH-4199. Azure Service Bus prefetch is a client-side buffer rather than a hard cap on unsettled
    /// deliveries, but it is still the bound an operator tunes and the one that governs how many messages can
    /// be sitting on this endpoint ahead of the handlers. Zero means prefetch is disabled, which is no ceiling
    /// at all, so report null rather than a denominator of nothing.
    /// </summary>
    public override int? InFlightLimit => PrefetchCount > 0 ? PrefetchCount : null;

    public int PrefetchCount
    {
        get
        {
            if (_prefetchCount.HasValue)
            {
                return _prefetchCount.Value;
            }

            // An explicit transport-wide setting is a deliberate choice and outranks any mode default
            if (Parent.ExplicitPrefetchCount.HasValue)
            {
                return Parent.ExplicitPrefetchCount.Value;
            }

            // GH-4051. Azure Service Bus's shipping default is 0 -- no prefetch at all -- which leaves a NativeAck
            // endpoint fetching one batch per network round trip and starving its lanes between receives. Sizing it
            // to the lanes is the same reasoning as RabbitMqQueue.PreFetchCount's NativeAck arm, but the ceiling
            // matters far more here than it does there: an Azure Service Bus message starts aging against its lock
            // the moment the CLIENT buffers it, and a prefetched message has no Envelope yet, so it is not being
            // tracked by LeaseRenewalTracker and cannot be renewed. Prefetch is therefore the one part of a
            // NativeAck endpoint's backlog that renewal does NOT protect -- which is exactly why this is a small
            // multiple of the lane count rather than the large buffer a throughput-only tuning would pick.
            if (Mode == EndpointMode.NativeAck)
            {
                var lanes = GroupShardingSlotNumber.HasValue
                    ? Math.Max((int)GroupShardingSlotNumber.Value, MaxDegreeOfParallelism)
                    : MaxDegreeOfParallelism;

                return lanes * 2;
            }

            return Parent.PrefetchCount;
        }
        set
        {
            if (value < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value,
                    "PrefetchCount cannot be negative");
            }

            _prefetchCount = value;
        }
    }

    /// <summary>
    /// GH-4051. How long Azure Service Bus holds the lock on one unsettled delivery from this endpoint. This is the
    /// clock <see cref="EndpointMode.NativeAck" /> has to keep alive: a NativeAck delivery stays unsettled for lane
    /// queue time <em>plus</em> handler time, and past this the broker takes the message back and redelivers it.
    /// Read from the entity's own <c>LockDuration</c> (Azure's default is one minute, its maximum five).
    /// </summary>
    /// <remarks>
    /// Wolverine reads the value it would <em>create</em> the entity with, which is the value the broker reports for
    /// any entity Wolverine provisioned. If an entity was created outside Wolverine with a SHORTER lock duration than
    /// the one configured here, renewal ticks at half of this rather than half of the real clock and can therefore
    /// tick too late; configure <c>Options.LockDuration</c> to match the deployed entity in that case.
    /// </remarks>
    internal virtual TimeSpan LockDuration => TimeSpan.FromMinutes(1);

    private TimeSpan _maximumLockRenewalDuration = TimeSpan.FromHours(1);

    /// <summary>
    ///     The longest a single delivery's lock is kept alive from its receipt under
    ///     <see cref="EndpointMode.NativeAck" /> before Wolverine stops renewing it and lets Azure Service Bus
    ///     redeliver. Default one hour.
    /// </summary>
    /// <remarks>
    ///     Unlike Amazon SQS -- where the equivalent ceiling exists because SQS refuses to keep a message invisible
    ///     for more than 12 hours -- Azure Service Bus imposes no cap on lock renewal at all; a message's lock can be
    ///     renewed until the message's own time-to-live runs out. This ceiling is therefore purely Wolverine's
    ///     stop-loss on a wedged handler, so that one stuck message cannot hold a broker lock forever. Reaching it is
    ///     deliberately NOT treated as a lost lease: the delivery may still finish inside the lock it already holds.
    /// </remarks>
    public TimeSpan MaximumLockRenewalDuration
    {
        get => _maximumLockRenewalDuration;
        set
        {
            if (value <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value,
                    "MaximumLockRenewalDuration must be greater than zero");
            }

            _maximumLockRenewalDuration = value;
        }
    }

    private int? _maximumConcurrentCalls;

    /// <summary>
    ///     How many messages an <c>Inline</c> or session-processor listener for this endpoint hands
    ///     to the handler pipeline at once. This maps to the Azure Service Bus SDK's
    ///     <c>MaxConcurrentCalls</c>, which Wolverine never set -- so inline Azure Service Bus
    ///     listeners ran strictly one message at a time on the SDK default, and the only way to
    ///     change that was the raw <see cref="ConfigureProcessor" /> hook. Null keeps the SDK
    ///     default of 1. Does not apply to the default Buffered/Durable batch receive loop, which
    ///     scales through MaximumParallelMessages instead. See GH-3494.
    /// </summary>
    /// <remarks>
    ///     GH-4051 considered giving this a <see cref="EndpointMode.NativeAck" /> default alongside
    ///     <see cref="PrefetchCount" /> and deliberately did not. This setting only reaches a
    ///     <c>ServiceBusProcessor</c> or <c>ServiceBusSessionProcessor</c>, and NativeAck runs on
    ///     <see cref="BatchedAzureServiceBusListener" />, which is built over a plain
    ///     <c>ServiceBusReceiver</c> and never sees these options at all. A default here would have been
    ///     inert configuration that reads as though it were doing something. NativeAck's lane count is
    ///     <c>MaximumParallelMessages</c> (or the partition slot count), and the only broker-side number that
    ///     has to keep up with it is the prefetch.
    /// </remarks>
    public int? MaximumConcurrentCalls
    {
        get => _maximumConcurrentCalls;
        set
        {
            if (value is < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value,
                    "MaximumConcurrentCalls must be at least 1");
            }

            _maximumConcurrentCalls = value;
        }
    }

    /// <summary>
    ///     Optional customization of the Azure Service Bus <see cref="ServiceBusProcessorOptions" /> used
    ///     by inline listeners for this endpoint. Wolverine reserves control of the properties it depends
    ///     on for correct message acknowledgement (see AzureServiceBusTransport.Listening), so those will
    ///     be re-asserted after this action runs.
    /// </summary>
    [IgnoreDescription]
    public Action<ServiceBusProcessorOptions>? ConfigureProcessor { get; set; }

    /// <summary>
    ///     Optional customization of the Azure Service Bus <see cref="ServiceBusSessionProcessorOptions" /> used
    ///     by session-enabled listeners for this endpoint. Setting this (directly, or the <c>SessionIds</c>
    ///     collection via <c>RequireSessionsWithOnlyTheseIdentifiers(...)</c>) switches the session listener away
    ///     from the default <c>AcceptNextSession</c> loop to a <see cref="ServiceBusSessionProcessor" />. Wolverine
    ///     reserves control of the properties it depends on for message acknowledgement (currently
    ///     <c>ReceiveMode</c> and <c>AutoCompleteMessages</c>), which are re-asserted after this action runs. Unlike
    ///     <see cref="ConfigureProcessor" />, this is a multicast delegate so the <c>SessionIds</c> sugar and any
    ///     explicit customization compose rather than overwrite each other.
    /// </summary>
    [IgnoreDescription]
    public Action<ServiceBusSessionProcessorOptions>? ConfigureSessionProcessor { get; set; }

    public abstract ValueTask<bool> CheckAsync();
    public abstract ValueTask TeardownAsync(ILogger logger);
    public abstract ValueTask SetupAsync(ILogger logger);
    public abstract bool IsPartitioned { get; }

    protected override bool supportsMode(EndpointMode mode)
    {
        return true;
    }

    /// <summary>
    /// GH-4049. Does this endpoint listen through Azure Service Bus sessions? Only queues and subscriptions
    /// carry a <c>RequiresSession</c> flag; a topic is never listened to directly.
    /// </summary>
    internal virtual bool RequiresSessions => false;

    /// <summary>
    /// GH-4049. Azure Service Bus sessions and <see cref="EndpointMode.NativeAck" /> are mutually exclusive, and
    /// the pair has to be refused after <see cref="Endpoint.Compile" /> rather than in the <see cref="Endpoint.Mode" />
    /// setter: <c>RequireSessions()</c> and <c>ProcessInParallelWithNativeAcks()</c> are both applied as delayed
    /// configuration, so whichever fluent call the setter happened to see first would decide whether the pair was
    /// caught. This runs over the final state instead.
    /// </summary>
    protected internal override IEnumerable<string> validateModeConfiguration()
    {
        if (Mode == EndpointMode.NativeAck && RequiresSessions)
        {
            yield return SessionsAreIncompatibleWithNativeAcks(this);
        }
    }

    /// <summary>
    /// GH-4049. Belt and braces with <see cref="validateModeConfiguration" />, which refuses this pairing for every
    /// listening endpoint at bootstrap. This one guards the listener selection itself, because
    /// <c>AzureServiceBusTransport</c> checks <c>RequiresSession</c> ahead of the mode and would otherwise take the
    /// session branch in silence -- and a session listener under native acks settles nothing at all.
    /// </summary>
    internal void AssertSessionsAreCompatibleWithMode()
    {
        if (Mode == EndpointMode.NativeAck && RequiresSessions)
        {
            throw new InvalidListenerConfigurationException(SessionsAreIncompatibleWithNativeAcks(this));
        }
    }

    internal static string SessionsAreIncompatibleWithNativeAcks(AzureServiceBusEndpoint endpoint)
    {
        return
            $"Invalid listener configuration for endpoint '{endpoint.Uri}': ProcessInParallelWithNativeAcks() was combined with " +
            "RequireSessions(). Azure Service Bus sessions hold a single lock over the whole session, and Wolverine's session " +
            "listener releases that lock as soon as it has handed the batch off -- which under native acks is before any handler " +
            "has run, so nothing could ever be acked. Sessions also exist to give per-session FIFO ordering, which native ack " +
            "lanes deliberately do not preserve. Use ProcessInline() with RequireSessions() for ordered session processing, or " +
            "drop RequireSessions() to use native acks with partitioned parallel processing.";
    }

    protected override AzureServiceBusEnvelopeMapper buildMapper(IWolverineRuntime runtime)
    {
        return new AzureServiceBusEnvelopeMapper(this, runtime);
    }

    public override string ToString()
    {
        return $"{GetType().Name}: {Uri}";
    }
}