using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.AzureServiceBus.Internal;

public class AzureServiceBusTopic : AzureServiceBusEndpoint
{
    public string TopicName { get; }

    public AzureServiceBusTopic(AzureServiceBusTransport parent, string topicName) : base(parent, new Uri($"{AzureServiceBusTransport.ProtocolName}://topic/{topicName}"), EndpointRole.Application)
    {
        if (parent == null)
        {
            throw new ArgumentNullException(nameof(parent));
        }

        TopicName = EndpointName = topicName ?? throw new ArgumentNullException(nameof(topicName));
    }

    public override ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver)
    {
        throw new NotSupportedException();
    }

    protected override ISender CreateSender(IWolverineRuntime runtime)
    {
        throw new NotImplementedException();
    }

    public override ValueTask<bool> CheckAsync()
    {
        throw new NotImplementedException();
    }

    public override ValueTask TeardownAsync(ILogger logger)
    {
        throw new NotImplementedException();
    }

    public override ValueTask SetupAsync(ILogger logger)
    {
        throw new NotImplementedException();
    }
}