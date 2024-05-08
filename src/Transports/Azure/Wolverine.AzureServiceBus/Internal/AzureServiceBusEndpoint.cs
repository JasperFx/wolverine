using Azure.Messaging.ServiceBus;
using JasperFx.Core;
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
}

public abstract class AzureServiceBusEndpoint : Endpoint, IBrokerEndpoint, IAzureServiceBusListeningEndpoint
{
    public AzureServiceBusEndpoint(AzureServiceBusTransport parent, Uri uri, EndpointRole role) : base(uri, role)
    {
        Parent = parent;
    }

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

    public abstract ValueTask<bool> CheckAsync();
    public abstract ValueTask TeardownAsync(ILogger logger);
    public abstract ValueTask SetupAsync(ILogger logger);

    protected override bool supportsMode(EndpointMode mode)
    {
        return true;
    }

    internal IAzureServiceBusEnvelopeMapper BuildMapper(IWolverineRuntime runtime)
    {
        if (Mapper != null) return Mapper;

        var mapper = new AzureServiceBusEnvelopeMapper(this, runtime);

        // Important for interoperability
        if (MessageType != null)
        {
            mapper.ReceivesMessage(MessageType);
        }

        return mapper;
    }

    public abstract Task<ServiceBusSessionReceiver> AcceptNextSessionAsync(CancellationToken cancellationToken);


    /// <summary>
    /// If specified, applies a custom envelope mapper to this endp[oint
    /// </summary>
    public IAzureServiceBusEnvelopeMapper? Mapper { get; set; }
}