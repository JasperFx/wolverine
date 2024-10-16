using Azure.Messaging.ServiceBus.Administration;
using JasperFx.Core;
using Wolverine.AzureServiceBus.Internal;
using Wolverine.Configuration;
using Wolverine.Transports;

namespace Wolverine.AzureServiceBus;

public class AzureServiceBusConfiguration : BrokerExpression<AzureServiceBusTransport, AzureServiceBusQueue,
    AzureServiceBusQueue, AzureServiceBusQueueListenerConfiguration, AzureServiceBusQueueSubscriberConfiguration,
    AzureServiceBusConfiguration>
{
    public AzureServiceBusConfiguration(AzureServiceBusTransport transport, WolverineOptions options) : base(transport,
        options)
    {
    }

    protected override AzureServiceBusQueueListenerConfiguration createListenerExpression(
        AzureServiceBusQueue listenerEndpoint)
    {
        return new AzureServiceBusQueueListenerConfiguration(listenerEndpoint);
    }

    protected override AzureServiceBusQueueSubscriberConfiguration createSubscriberExpression(
        AzureServiceBusQueue subscriberEndpoint)
    {
        return new AzureServiceBusQueueSubscriberConfiguration(subscriberEndpoint);
    }

    /// <summary>
    ///     Add explicit configuration to an AzureServiceBus queue that is being created by
    ///     this application
    /// </summary>
    /// <param name="queueName"></param>
    /// <param name="configure"></param>
    /// <returns></returns>
    public AzureServiceBusConfiguration ConfigureQueue(string queueName, Action<CreateQueueOptions> configure)
    {
        if (configure == null)
        {
            throw new ArgumentNullException(nameof(configure));
        }

        var queue = Transport.Queues[queueName];
        configure(queue.Options);

        return this;
    }

    /// <summary>
    /// Opt into using conventional message routing using topics and topic
    /// subscriptions based on message type names
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public AzureServiceBusConfiguration UseTopicAndSubscriptionConventionalRouting(
        Action<AzureServiceBusTopicBroadcastingRoutingConvention>? configure = null)
    {
        var routing = new AzureServiceBusTopicBroadcastingRoutingConvention();
        configure?.Invoke(routing);

        Options.RouteWith(routing);

        return this;
    }

    /// <summary>
    /// Opt into using conventional message routing using
    /// queues based on message type names
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    public AzureServiceBusConfiguration UseConventionalRouting(
        Action<AzureServiceBusMessageRoutingConvention>? configure = null)
    {
        var routing = new AzureServiceBusMessageRoutingConvention();
        configure?.Invoke(routing);

        Options.RouteWith(routing);

        return this;
    }

    /// <summary>
    /// Is Wolverine enabled to create system queues automatically for responses and retries? This
    /// should probably be set to false if the application does not have permissions to create queues
    /// </summary>
    /// <param name="enabled"></param>
    /// <returns></returns>
    public AzureServiceBusConfiguration SystemQueuesAreEnabled(bool enabled)
    {
        Transport.SystemQueuesEnabled = enabled;
        return this;
    }
    
    /// <summary>
    /// Utilize an Azure Service Bus queue as the control queue between Wolverine nodes
    /// This is more efficient than the built in Wolverine database control
    /// queues if Azure Service Bus is an option
    /// </summary>
    /// <returns></returns>
    public AzureServiceBusConfiguration EnableWolverineControlQueues()
    {
        var queueName = "wolverine.control." + Options.Durability.AssignedNodeNumber;
        
        var queue = Transport.Queues[queueName];

        queue.Options.AutoDeleteOnIdle = 5.Minutes();
        queue.Mode = EndpointMode.BufferedInMemory;
        queue.IsListener = true;
        queue.EndpointName = "Control";
        queue.IsUsedForReplies = true;
        queue.Role = EndpointRole.System;

        Options.Transports.NodeControlEndpoint = queue;

        return this;
    }
}