using Azure.Messaging.ServiceBus;
using Baseline.Dates;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.AzureServiceBus.Internal;

public interface IAzureServiceBusListeningEndpoint
{
    /// <summary>
    /// The maximum number of messages to receive in a single batch when listening
    /// in either buffered or durable modes. The default is 20.
    /// </summary>
    public int MaximumMessagesToReceive { get; set; }
    
    /// <summary>
    /// The duration for which the listener waits for a message to arrive in the
    /// queue before returning. If a message is available, the call returns sooner than this time.
    /// If no messages are available and the wait time expires, the call returns successfully
    /// with an empty list of messages. Default is 5 seconds.
    /// </summary>
    public TimeSpan MaximumWaitTime { get; set; } 
}

public abstract class AzureServiceBusEndpoint : Endpoint, IBrokerEndpoint, IAzureServiceBusListeningEndpoint
{
    public AzureServiceBusTransport Parent { get; }
    
    /// <summary>
    /// The maximum number of messages to receive in a single batch when listening
    /// in either buffered or durable modes. The default is 20.
    /// </summary>
    public int MaximumMessagesToReceive { get; set; } = 20;
    
    /// <summary>
    /// The duration for which the listener waits for a message to arrive in the
    /// queue before returning. If a message is available, the call returns sooner than this time.
    /// If no messages are available and the wait time expires, the call returns successfully
    /// with an empty list of messages. Default is 5 seconds.
    /// </summary>
    public TimeSpan MaximumWaitTime { get; set; } = 5.Seconds();
    

    public AzureServiceBusEndpoint(AzureServiceBusTransport parent, Uri uri, EndpointRole role) : base(uri, role)
    {
        Parent = parent;
    }

    protected override bool supportsMode(EndpointMode mode)
    {
        return mode != EndpointMode.Inline;
    }

    public abstract ValueTask<bool> CheckAsync();
    public abstract ValueTask TeardownAsync(ILogger logger);
    public abstract ValueTask SetupAsync(ILogger logger);
    
    

    internal IEnvelopeMapper<ServiceBusReceivedMessage, ServiceBusMessage> BuildMapper(IWolverineRuntime runtime)
    {
        var mapper = new AzureServiceBusEnvelopeMapper(this, runtime);

        return mapper;
    }
}