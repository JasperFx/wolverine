using Azure.Messaging.ServiceBus;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.Logging;
using Wolverine.AzureServiceBus.Internal;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.AzureServiceBus;

public partial class AzureServiceBusTransport
{
    internal Task<ServiceBusSessionReceiver> AcceptNextSessionAsync(AzureServiceBusEndpoint endpoint, CancellationToken cancellationToken)
    {
        if (endpoint is AzureServiceBusQueue queue)
        {
            return BusClient.AcceptNextSessionAsync(queue.QueueName, cancellationToken: cancellationToken);
        }

        if (endpoint is AzureServiceBusSubscription subscription)
        {
            return BusClient.AcceptNextSessionAsync(subscription.Topic.TopicName, subscription.SubscriptionName,
                cancellationToken: cancellationToken);
        }

        throw new ArgumentOutOfRangeException(nameof(endpoint),
            "This usage only works with queues or subscriptions, but got " + endpoint.GetType().FullNameInCode());

    }

    internal async ValueTask<IListener> BuildListenerForQueue(IWolverineRuntime runtime, IReceiver receiver, AzureServiceBusQueue queue)
    {
        var mapper = queue.BuildMapper(runtime);

        var listener = await buildListenerForQueue(runtime, receiver, queue, mapper);

        if (Tenants.Any())
        {
            var compound = new CompoundListener(queue.Uri);
            compound.Inner.Add(listener);

            foreach (var tenant in Tenants)
            {
                var rule = new TenantIdRule(tenant.TenantId);
                var wrapped = new ReceiverWithRules(receiver, [rule]);
                var tenantListener = await tenant.Transport.buildListenerForQueue(runtime, wrapped, queue, mapper);
                compound.Inner.Add(tenantListener);
            }

            return compound;
        }

        return listener;
    }

    private async Task<IListener> buildListenerForQueue(IWolverineRuntime runtime, IReceiver receiver, AzureServiceBusQueue queue,
        IAzureServiceBusEnvelopeMapper mapper)
    {
        var requeue = BuildInlineSenderForQueue(runtime, queue);

        if (queue.Options.RequiresSession)
        {
            return new AzureServiceBusSessionListener(this, queue, receiver, mapper,
                runtime.LoggerFactory.CreateLogger<AzureServiceBusSessionListener>(), requeue);
        }

        if (queue.Mode == EndpointMode.Inline)
        {
            var messageProcessor = BusClient.CreateProcessor(queue.QueueName);

            var inlineListener = new InlineAzureServiceBusListener(queue,
                runtime.LoggerFactory.CreateLogger<InlineAzureServiceBusListener>(), messageProcessor, receiver,
                mapper,
                requeue);

            await inlineListener.StartAsync();

            return inlineListener;
        }

        var messageReceiver = BusClient.CreateReceiver(queue.QueueName);
        var logger = runtime.LoggerFactory.CreateLogger<BatchedAzureServiceBusListener>();
        var listener = new BatchedAzureServiceBusListener(queue, logger, receiver, messageReceiver, mapper, requeue);

        return listener;
    }

    public async ValueTask<IListener> BuildListenerForSubscription(IWolverineRuntime runtime, IReceiver receiver, AzureServiceBusSubscription subscription)
    {
        var mapper = subscription.BuildMapper(runtime);
        var listener = await buildListenerForSubscription(runtime, receiver, subscription, mapper);
        if (Tenants.Any())
        {
            var compound = new CompoundListener(subscription.Uri);
            compound.Inner.Add(listener);

            foreach (var tenant in Tenants)
            {
                var rule = new TenantIdRule(tenant.TenantId);
                var wrapped = new ReceiverWithRules(receiver, [rule]);
                var tenantListener = await tenant.Transport.buildListenerForSubscription(runtime, wrapped, subscription, mapper);
                compound.Inner.Add(tenantListener);
            }

            return compound;
        }

        return listener;
    }

    private async Task<IListener> buildListenerForSubscription(IWolverineRuntime runtime, IReceiver receiver,
        AzureServiceBusSubscription subscription, IAzureServiceBusEnvelopeMapper mapper)
    {
        var requeue = RetryQueue != null ? BuildInlineSenderForQueue(runtime, RetryQueue) : BuildInlineSenderForTopic(runtime, subscription.Topic);
        

        if (subscription.Options.RequiresSession)
        {
            return new AzureServiceBusSessionListener(this, subscription, receiver, mapper,
                runtime.LoggerFactory.CreateLogger<AzureServiceBusSessionListener>(), requeue);
        }

        if (subscription.Mode == EndpointMode.Inline)
        {
            var messageProcessor = BusClient.CreateProcessor(subscription.Topic.TopicName, subscription.SubscriptionName);
            var inlineListener = new InlineAzureServiceBusListener(subscription,
                runtime.LoggerFactory.CreateLogger<InlineAzureServiceBusListener>(), messageProcessor, receiver, mapper,  requeue
            );

            await inlineListener.StartAsync();

            return inlineListener;
        }

        var messageReceiver = BusClient.CreateReceiver(subscription.Topic.TopicName, subscription.SubscriptionName);

        var listener = new BatchedAzureServiceBusListener(subscription, runtime.LoggerFactory.CreateLogger<BatchedAzureServiceBusListener>(), receiver, messageReceiver, mapper, requeue);

        return listener;
    }
}