using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.AzureServiceBus.Internal;

public class AzureServiceBusQueue : AzureServiceBusEndpoint, IBrokerQueue
{
    public AzureServiceBusQueue(AzureServiceBusTransport parent, string queueName, EndpointRole role = EndpointRole.Application) : base(parent, new Uri($"{AzureServiceBusTransport.ProtocolName}://queue/{queueName}"), role)
    {
    }

    public override ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver)
    {
        throw new NotImplementedException();
    }

    protected override ISender CreateSender(IWolverineRuntime runtime)
    {
        throw new NotImplementedException();
    }

    public async ValueTask PurgeAsync(ILogger logger)
    {
        throw new NotImplementedException();
    }

    public async ValueTask<Dictionary<string, string>> GetAttributesAsync()
    {
        throw new NotImplementedException();
    }
}

public class AzureServiceBusTopic : AzureServiceBusEndpoint
{
    public string TopicName { get; }

    public AzureServiceBusTopic(AzureServiceBusTransport parent, string topicName) : base(parent, new Uri($"{AzureServiceBusTransport.ProtocolName}://topic/{topicName}"), EndpointRole.Application)
    {
        TopicName = topicName;
    }

    public override ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver)
    {
        throw new NotSupportedException();
    }

    protected override ISender CreateSender(IWolverineRuntime runtime)
    {
        throw new NotImplementedException();
    }
}

public class AzureServiceBusSubscription : AzureServiceBusEndpoint
{
    public string SubscriptionName { get; }

    public AzureServiceBusSubscription(AzureServiceBusTransport parent, AzureServiceBusTopic topic, string subscriptionName) : base(parent, new Uri($"{AzureServiceBusTransport.ProtocolName}://topic/{topic.TopicName}/{subscriptionName}"), EndpointRole.Application)
    {
        SubscriptionName = subscriptionName;
    }

    public override async ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver)
    {
        throw new NotImplementedException();
    }

    protected override ISender CreateSender(IWolverineRuntime runtime)
    {
        throw new NotImplementedException();
    }
}