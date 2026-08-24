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
    public int PrefetchCount
    {
        get => _prefetchCount ?? Parent.PrefetchCount;
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