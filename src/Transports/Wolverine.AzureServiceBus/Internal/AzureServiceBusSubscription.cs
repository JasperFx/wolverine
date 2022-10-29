using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.AzureServiceBus.Internal;

public class AzureServiceBusSubscription : AzureServiceBusEndpoint
{
    public string SubscriptionName { get; }

    public AzureServiceBusSubscription(AzureServiceBusTransport parent, AzureServiceBusTopic topic, string subscriptionName) : base(parent, new Uri($"{AzureServiceBusTransport.ProtocolName}://topic/{topic.TopicName}/{subscriptionName}"), EndpointRole.Application)
    {
        if (parent == null)
        {
            throw new ArgumentNullException(nameof(parent));
        }

        SubscriptionName = EndpointName = subscriptionName;
        Topic = topic ?? throw new ArgumentNullException(nameof(topic));
    }

    public AzureServiceBusTopic Topic { get; }

    public override async ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver)
    {
        throw new NotImplementedException();
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