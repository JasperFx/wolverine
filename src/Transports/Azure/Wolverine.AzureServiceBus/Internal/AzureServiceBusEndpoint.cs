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

    /// <summary>
    ///     Optional customization of the Azure Service Bus <see cref="ServiceBusProcessorOptions" /> used
    ///     by inline listeners for this endpoint. Wolverine reserves control of the properties it depends
    ///     on for correct message acknowledgement (see AzureServiceBusTransport.Listening), so those will
    ///     be re-asserted after this action runs.
    /// </summary>
    [IgnoreDescription]
    public Action<ServiceBusProcessorOptions>? ConfigureProcessor { get; set; }

    public abstract ValueTask<bool> CheckAsync();
    public abstract ValueTask TeardownAsync(ILogger logger);
    public abstract ValueTask SetupAsync(ILogger logger);
    public abstract bool IsPartitioned { get; }

    protected override bool supportsMode(EndpointMode mode)
    {
        return true;
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