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
        var mapper = topic.BuildMapper(runtime);

        var defaultSender = buildSenderForTopic(runtime, topic, mapper);

        if (Tenants.Any() && topic.TenancyBehavior == TenancyBehavior.TenantAware)
        {
            var tenantedSender = new TenantedSender(topic.Uri, TenantedIdBehavior, defaultSender);
            foreach (var tenant in Tenants)
            {
                var sender = tenant.Transport.buildSenderForTopic(runtime, topic, mapper);
                tenantedSender.RegisterSender(tenant.TenantId, sender);
            }

            return tenantedSender;
        }

        return defaultSender;
    }

    private ISender buildSenderForTopic(IWolverineRuntime runtime, AzureServiceBusTopic topic,
        IAzureServiceBusEnvelopeMapper mapper)
    {
        var sender = BusClient.CreateSender(topic.TopicName);

        if (topic.Mode == EndpointMode.Inline)
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
        var mapper = queue.BuildMapper(runtime);
        var defaultSender = buildSenderForQueue(runtime, queue, mapper);

        if (Tenants.Any() && queue.TenancyBehavior == TenancyBehavior.TenantAware)
        {
            var tenantedSender = new TenantedSender(queue.Uri, TenantedIdBehavior, defaultSender);
            foreach (var tenant in Tenants)
            {
                var sender = tenant.Transport.buildSenderForQueue(runtime, queue, mapper);
                tenantedSender.RegisterSender(tenant.TenantId, sender);
            }

            return tenantedSender;
        }
        
        return defaultSender;
    }

    private ISender buildSenderForQueue(IWolverineRuntime runtime, AzureServiceBusQueue queue,
        IAzureServiceBusEnvelopeMapper mapper)
    {
        var sender = BusClient.CreateSender(queue.QueueName);

        if (queue.Mode == EndpointMode.Inline)
        {
            var inlineSender = new InlineAzureServiceBusSender(queue, mapper, sender,
                runtime.LoggerFactory.CreateLogger<InlineAzureServiceBusSender>(), runtime.Cancellation);

            return inlineSender;
        }

        var protocol = new AzureServiceBusSenderProtocol(runtime, queue, mapper, sender);

        return new BatchedSender(queue, protocol, runtime.DurabilitySettings.Cancellation, runtime.LoggerFactory.CreateLogger<AzureServiceBusSenderProtocol>());
    }

}