using Azure.Messaging.ServiceBus.Administration;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.AzureServiceBus.Internal;
using Wolverine.Configuration;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

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
    /// Override the sending logic behavior for unknown or missing tenant ids when
    /// using multi-tenanted namespaces
    /// </summary>
    /// <param name="tenantedIdBehavior"></param>
    /// <returns></returns>
    public AzureServiceBusConfiguration TenantIdBehavior(TenantedIdBehavior tenantedIdBehavior)
    {
        Transport.TenantedIdBehavior = tenantedIdBehavior;
        return this;
    }

    /// <summary>
    /// Add a connection to a different Azure Service Bus broker for the named tenant using a fully
    /// qualified namespace
    /// </summary>
    /// <param name="tenantId"></param>
    /// <param name="fullyQualifiedNamespace"></param>
    /// <returns></returns>
    public AzureServiceBusConfiguration AddTenantByNamespace(string tenantId, string fullyQualifiedNamespace)
    {
        if (tenantId.IsEmpty()) throw new ArgumentOutOfRangeException(nameof(tenantId), "Empty or null tenantId");
        if (fullyQualifiedNamespace.IsEmpty()) throw new ArgumentOutOfRangeException(nameof(fullyQualifiedNamespace), "Empty or null namespace");
        var azureServiceBusTenant = Transport.Tenants[tenantId];
        azureServiceBusTenant.Transport.FullyQualifiedNamespace = fullyQualifiedNamespace;
        
        return this;
    }

    /// <summary>
    /// Add a connection to a different Azure Service Bus broker for the named tenant using a connection string
    /// </summary>
    /// <param name="tenantId"></param>
    /// <param name="connectionString"></param>
    /// <returns></returns>
    public AzureServiceBusConfiguration AddTenantByConnectionString(string tenantId, string connectionString)
    {
        Transport.Tenants[tenantId].Transport.ConnectionString = connectionString;
        return this;
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
    /// Opt into using conventional message routing using topics and topic
    /// subscriptions with the specified naming source.
    /// Using <see cref="NamingSource.FromHandlerType"/> is appropriate for modular monolith
    /// scenarios where you have more than one handler for a given message type.
    /// </summary>
    /// <param name="namingSource"></param>
    /// <param name="configure"></param>
    /// <returns></returns>
    public AzureServiceBusConfiguration UseTopicAndSubscriptionConventionalRouting(NamingSource namingSource,
        Action<AzureServiceBusTopicBroadcastingRoutingConvention>? configure = null)
    {
        var routing = new AzureServiceBusTopicBroadcastingRoutingConvention();
        routing.UseNaming(namingSource);
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
    /// Opt into using conventional message routing using
    /// queues with the specified naming source.
    /// Using <see cref="NamingSource.FromHandlerType"/> is appropriate for modular monolith
    /// scenarios where you have more than one handler for a given message type.
    /// </summary>
    /// <param name="namingSource"></param>
    /// <param name="configure"></param>
    /// <returns></returns>
    public AzureServiceBusConfiguration UseConventionalRouting(NamingSource namingSource,
        Action<AzureServiceBusMessageRoutingConvention>? configure = null)
    {
        var routing = new AzureServiceBusMessageRoutingConvention();
        routing.UseNaming(namingSource);
        configure?.Invoke(routing);

        Options.RouteWith(routing);

        return this;
    }

    /// <summary>
    /// Enable a background listener that drains the native Azure Service Bus dead letter sub-queues
    /// (<c>$DeadLetterQueue</c>) of every listening queue and subscription, recovering the messages
    /// into Wolverine's durable dead letter storage (the <c>wolverine_dead_letters</c> table). This
    /// makes natively dead-lettered messages queryable and replayable through <c>IDeadLetters</c>
    /// and tools like CritterWatch. It is the Azure Service Bus analogue of RabbitMQ's
    /// <c>EnableDeadLetterQueueRecovery()</c>, and reads the native
    /// <c>DeadLetterReason</c>/<c>DeadLetterErrorDescription</c> as the recorded failure metadata.
    ///
    /// Requires Wolverine's durable message storage (a database) to be configured.
    /// </summary>
    /// <returns></returns>
    public AzureServiceBusConfiguration EnableDeadLetterQueueRecovery()
    {
        ensureRecoveryServicesRegistered();
        return this;
    }

    /// <summary>
    /// Enable a background listener that drains the native Azure Service Bus dead letter sub-queues
    /// of only the named queues (or subscription endpoint names), recovering the messages into
    /// Wolverine's durable dead letter storage.
    /// </summary>
    /// <param name="queueOrSubscriptionNames">
    /// The queue names — or subscription endpoint names — whose native dead letter sub-queues should
    /// be drained.
    /// </param>
    /// <returns></returns>
    public AzureServiceBusConfiguration EnableDeadLetterQueueRecovery(params string[] queueOrSubscriptionNames)
    {
        var settings = ensureRecoveryServicesRegistered();
        foreach (var name in queueOrSubscriptionNames)
        {
            if (!settings.EndpointNames.Contains(name))
            {
                settings.EndpointNames.Add(name);
            }
        }

        return this;
    }

    private AzureServiceBusDeadLetterQueueRecoverySettings ensureRecoveryServicesRegistered()
    {
        var existing = Options.Services
            .Where(s => s.ServiceType == typeof(AzureServiceBusDeadLetterQueueRecoverySettings))
            .Select(s => s.ImplementationInstance)
            .OfType<AzureServiceBusDeadLetterQueueRecoverySettings>()
            .FirstOrDefault();

        if (existing != null)
        {
            return existing;
        }

        var settings = new AzureServiceBusDeadLetterQueueRecoverySettings();
        Options.Services.AddSingleton(settings);
        Options.Services.AddSingleton(Transport);
        Options.Services.AddHostedService<AzureServiceBusDeadLetterQueueListener>();
        return settings;
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
        // In Solo mode the assigned node number is always 1 (#3188); key the per-node control queue
        // on the unique node id so multiple Solo hosts on one namespace don't collide. See #3189.
        var controlNode = Options.Durability.Mode == DurabilityMode.Solo
            ? Options.UniqueNodeId.ToString("N")
            : Options.Durability.AssignedNodeNumber.ToString();
        var queueName = "wolverine.control." + controlNode;
        
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