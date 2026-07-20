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

        if (Tenants.Any() && queue.TenancyBehavior == TenancyBehavior.TenantAware)
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
            // GH-3533: when the endpoint carries any ServiceBusSessionProcessorOptions customization
            // (most importantly SessionIds pinning), use the SDK's ServiceBusSessionProcessor instead
            // of the default AcceptNextSession loop. Gated so current session listeners are unchanged.
            if (queue.ConfigureSessionProcessor != null)
            {
                var sessionProcessor =
                    BusClient.CreateSessionProcessor(queue.QueueName, BuildSessionProcessorOptions(queue));

                var sessionListener = new InlineAzureServiceBusSessionListener(queue,
                    runtime.LoggerFactory.CreateLogger<InlineAzureServiceBusSessionListener>(), sessionProcessor,
                    receiver, mapper, requeue);

                await sessionListener.StartAsync();

                return sessionListener;
            }

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
        if (Tenants.Any() && subscription.TenancyBehavior == TenancyBehavior.TenantAware)
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
            // GH-3533: see buildListenerForQueue -- the session processor path is opt-in via
            // ConfigureSessionProcessor (e.g. RequireSessionsWithOnlyTheseIdentifiers).
            if (subscription.ConfigureSessionProcessor != null)
            {
                var sessionProcessor = BusClient.CreateSessionProcessor(subscription.Topic.TopicName,
                    subscription.SubscriptionName, BuildSessionProcessorOptions(subscription));

                var sessionListener = new InlineAzureServiceBusSessionListener(subscription,
                    runtime.LoggerFactory.CreateLogger<InlineAzureServiceBusSessionListener>(), sessionProcessor,
                    receiver, mapper, requeue);

                await sessionListener.StartAsync();

                return sessionListener;
            }

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

    // Builds the ServiceBusSessionProcessorOptions for the opt-in ServiceBusSessionProcessor session
    // listener (GH-3533). Applies the user's (multicast) customization -- including any SessionIds
    // pinning -- then re-asserts the acknowledgement properties Wolverine's
    // InlineAzureServiceBusSessionListener depends on.
    internal static ServiceBusSessionProcessorOptions BuildSessionProcessorOptions(AzureServiceBusEndpoint endpoint)
    {
        var options = new ServiceBusSessionProcessorOptions
        {
            // Map the existing "parallel sessions" knob (ListenerCount, set via RequireSessions(count))
            // onto the processor's concurrency. A user may override this in ConfigureSessionProcessor.
            MaxConcurrentSessions = endpoint.ListenerCount > 0 ? endpoint.ListenerCount : 1,

            // Preserve the in-session FIFO ordering the hand-rolled loop provided
            MaxConcurrentCallsPerSession = 1
        };

        endpoint.ConfigureSessionProcessor?.Invoke(options);

        // Reserved by Wolverine: the listener relies on the peek-lock model to explicitly complete,
        // defer, and dead letter messages, so these cannot be honored from user configuration.
        options.ReceiveMode = ServiceBusReceiveMode.PeekLock;
        options.AutoCompleteMessages = false;

        return options;
    }
}