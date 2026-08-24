using Microsoft.Extensions.Logging;
using Wolverine.AzureServiceBus.Internal;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports.Sending;

namespace Wolverine.AzureServiceBus;

public partial class AzureServiceBusTransport
{
    internal ISender CreateSender(IWolverineRuntime runtime, AzureServiceBusTopic topic)
    {
        // GH-3826: when tenants are in play, every sender underneath the TenantedSender -- the
        // default fallback included -- has to be the fire-and-forget inline sender. TenantedSender
        // deliberately does not implement ISenderRequiresCallback (GH-2361), so EndpointCollection
        // never calls RegisterCallback on the senders beneath it, and a BatchedSender there throws
        // "This sender has not been registered." on every single batch. Same reasoning and same
        // shape as Redis, MQTT, and Pub/Sub.
        if (Tenants.Any() && topic.TenancyBehavior == TenancyBehavior.TenantAware)
        {
            return BuildInlineSenderForTopic(runtime, topic);
        }

        var mapper = topic.BuildMapper(runtime);

        return buildSenderForTopic(runtime, topic, mapper);
    }

    private ISender buildSenderForTopic(IWolverineRuntime runtime, AzureServiceBusTopic topic,
        IAzureServiceBusEnvelopeMapper mapper)
    {
        var sender = BusClient.CreateSender(topic.TopicName);

        if (topic.SendsInline)
        {
            var inlineSender = new InlineAzureServiceBusSender(topic, mapper, sender,
                runtime.LoggerFactory.CreateLogger<InlineAzureServiceBusSender>(), runtime.Cancellation);

            return inlineSender;
        }

        var protocol = new AzureServiceBusSenderProtocol(runtime, topic, mapper, sender);

        return new BatchedSender(topic, protocol, runtime.DurabilitySettings.Cancellation, runtime.LoggerFactory.CreateLogger<AzureServiceBusSenderProtocol>());
    }

    internal ISender BuildInlineSenderForTopic(IWolverineRuntime runtime, AzureServiceBusTopic topic)
    {
        var mapper = topic.BuildMapper(runtime);
        
        var defaultSender = buildInlineSenderForTopic(runtime, topic, mapper);
        
        if (Tenants.Any() && topic.TenancyBehavior == TenancyBehavior.TenantAware)
        {
            var tenantedSender = new TenantedSender(topic.Uri, TenantedIdBehavior, defaultSender);
            foreach (var tenant in Tenants)
            {
                var sender = tenant.Transport.buildInlineSenderForTopic(runtime, topic, mapper);
                tenantedSender.RegisterSender(tenant.TenantId, sender);
            }

            return tenantedSender;
        }
        
        return defaultSender;
    }

    private ISender buildInlineSenderForTopic(IWolverineRuntime runtime, AzureServiceBusTopic topic,
        IAzureServiceBusEnvelopeMapper mapper)
    {
        var sender = BusClient.CreateSender(topic.TopicName);
        return new InlineAzureServiceBusSender(topic, mapper, sender,
            runtime.LoggerFactory.CreateLogger<InlineAzureServiceBusSender>(), runtime.Cancellation);
    }

    internal ISender BuildInlineSenderForQueue(IWolverineRuntime runtime, AzureServiceBusQueue queue)
    {
        var mapper = queue.BuildMapper(runtime);
        var defaultSender = buildInlineSenderForQueue(runtime, queue, mapper);

        if (Tenants.Any() && queue.TenancyBehavior == TenancyBehavior.TenantAware)
        {
            var tenantedSender = new TenantedSender(queue.Uri, TenantedIdBehavior, defaultSender);
            foreach (var tenant in Tenants)
            {
                var sender = tenant.Transport.buildInlineSenderForQueue(runtime, queue, mapper);
                tenantedSender.RegisterSender(tenant.TenantId, sender);
            }

            return tenantedSender;
        }

        return defaultSender;
    }

    private ISender buildInlineSenderForQueue(IWolverineRuntime runtime, AzureServiceBusQueue queue,
        IAzureServiceBusEnvelopeMapper mapper)
    {
        var sender = BusClient.CreateSender(queue.QueueName);
        return new InlineAzureServiceBusSender(queue, mapper, sender,
            runtime.LoggerFactory.CreateLogger<InlineAzureServiceBusSender>(), runtime.Cancellation);
    }

    internal ISender BuildSenderForQueue(IWolverineRuntime runtime, AzureServiceBusQueue queue)
    {
        // GH-3826: see CreateSender(topic) -- a BatchedSender underneath a TenantedSender never
        // receives its ISenderCallback and fails every batch, so the tenanted path is inline only.
        if (Tenants.Any() && queue.TenancyBehavior == TenancyBehavior.TenantAware)
        {
            return BuildInlineSenderForQueue(runtime, queue);
        }

        var mapper = queue.BuildMapper(runtime);

        return buildSenderForQueue(runtime, queue, mapper);
    }

    private ISender buildSenderForQueue(IWolverineRuntime runtime, AzureServiceBusQueue queue,
        IAzureServiceBusEnvelopeMapper mapper)
    {
        var sender = BusClient.CreateSender(queue.QueueName);

        if (queue.SendsInline)
        {
            var inlineSender = new InlineAzureServiceBusSender(queue, mapper, sender,
                runtime.LoggerFactory.CreateLogger<InlineAzureServiceBusSender>(), runtime.Cancellation);

            return inlineSender;
        }

        var protocol = new AzureServiceBusSenderProtocol(runtime, queue, mapper, sender);

        return new BatchedSender(queue, protocol, runtime.DurabilitySettings.Cancellation, runtime.LoggerFactory.CreateLogger<AzureServiceBusSenderProtocol>());
    }

}