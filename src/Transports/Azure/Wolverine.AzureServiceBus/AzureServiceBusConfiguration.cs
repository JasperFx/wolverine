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
    /// CAUTION!!! This directs Wolverine to delete *every* queue and topic in the connected Azure Service
    /// Bus namespace at application start up, before any objects are provisioned. This is destructive and
    /// irreversible, and is only meant for local development or automated testing against the Azure Service
    /// Bus emulator. Never enable this against a real Azure Service Bus namespace that holds anything you
    /// care about.
    /// </summary>
    /// <returns></returns>
    public AzureServiceBusConfiguration DeleteAllExistingObjectsOnStartup()
    {
        Transport.DeleteAllExistingObjectsOnStartup = true;
        return this;
    }

    /// <summary>
    ///     Set the transport-wide default for the number of messages that the underlying Azure
    ///     Service Bus receivers eagerly buffer on the client ahead of processing. Applies to every
    ///     Azure Service Bus listening endpoint that does not override PrefetchCount itself. The
    ///     default is 0 (prefetch is disabled). Prefetched messages age against the message lock
    ///     duration while they sit in the client buffer, so size this relative to
    ///     MaximumMessagesToReceive and your handler latency
    /// </summary>
    /// <param name="prefetchCount">The client-side prefetch count. Must be non-negative</param>
    /// <returns></returns>
    public AzureServiceBusConfiguration PrefetchCount(int prefetchCount)
    {
        Transport.PrefetchCount = prefetchCount;
        return this;
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
        var routing = new AzureServiceBusTopicBroadcastingRoutingConvention
        {
            BoundTransport = Transport
        };
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
        var routing = new AzureServiceBusTopicBroadcastingRoutingConvention
        {
            BoundTransport = Transport
        };
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
        var routing = new AzureServiceBusMessageRoutingConvention
        {
            BoundTransport = Transport
        };
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
        var routing = new AzureServiceBusMessageRoutingConvention
        {
            BoundTransport = Transport
        };
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
    /// Prepend a prefix to the names of the queues that Wolverine creates for its own use, so that
    /// several unrelated applications can share one Azure Service Bus namespace without colliding.
    /// With a prefix of "my-project" the system queues become
    /// "my-project.wolverine.response.{ServiceName}.{node}", "my-project.wolverine.retries.{servicename}",
    /// "my-project.wolverine.control.{node}", and "my-project.wolverine-dead-letter-queue". Without a
    /// prefix -- the default -- those names are exactly what they have always been.
    ///
    /// <para>
    /// Only queues that Wolverine names for itself are affected. A dead letter queue you name
    /// yourself, through either <c>ConfigureDeadLetterQueue("x")</c> on an endpoint or
    /// <see cref="DefaultDeadLetterQueueName"/> on the transport, is taken as fully qualified and is
    /// never prefixed.
    /// </para>
    ///
    /// <para>
    /// This is a separate concept from <c>PrefixIdentifiers()</c>, which renames the *application*
    /// queues and topics and deliberately does not reach Wolverine's system queues. The two can be
    /// combined: use <c>PrefixIdentifiers()</c> when every application queue should be renamed too,
    /// and this when cooperating applications must keep sharing the same application queue names
    /// while still each owning their own control, response, retry and dead letter queues.
    /// </para>
    ///
    /// <para>
    /// Order does not matter with respect to <see cref="EnableWolverineControlQueues"/>: calling
    /// this afterwards rebuilds the control queue under the prefixed name.
    /// </para>
    /// </summary>
    /// <param name="prefix">
    /// The prefix. Sanitized to legal Azure Service Bus entity characters, and any trailing
    /// delimiter is trimmed
    /// </param>
    /// <returns></returns>
    public AzureServiceBusConfiguration SystemQueuePrefix(string prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix))
        {
            throw new ArgumentException("The system queue prefix must be a non-empty value", nameof(prefix));
        }

        var sanitized = Transport.SanitizeIdentifier(prefix.Trim()).TrimEnd('.', '/');
        if (sanitized.IsEmpty())
        {
            throw new ArgumentException(
                $"'{prefix}' does not leave any usable Azure Service Bus entity name characters to use as a system queue prefix",
                nameof(prefix));
        }

        Transport.SystemQueuePrefix = sanitized;

        // The control queue has to be built eagerly (see EnableWolverineControlQueues below), so if it
        // was already built under the unprefixed name, rebuild it here under the new one.
        if (Transport.ControlQueue != null)
        {
            buildControlQueue();
        }

        return this;
    }

    /// <summary>
    /// Override the transport wide default dead letter queue name, which is otherwise
    /// "wolverine-dead-letter-queue" with any <see cref="SystemQueuePrefix"/> prepended. The name
    /// given here is taken as fully qualified and is never prefixed.
    ///
    /// <para>
    /// This is only the default. Per endpoint configuration still wins:
    /// <c>ConfigureDeadLetterQueue("x")</c> names a different dead letter queue for one endpoint, and
    /// <c>DisableDeadLetterQueueing()</c> turns the native dead letter queue off for one endpoint
    /// regardless of what is set here.
    /// </para>
    /// </summary>
    /// <param name="queueName">
    /// The default dead letter queue name. Must be non-null and non-empty; call
    /// <c>DisableDeadLetterQueueing()</c> on an endpoint to turn dead letter queueing off instead
    /// </param>
    /// <returns></returns>
    public AzureServiceBusConfiguration DefaultDeadLetterQueueName(string queueName)
    {
        if (string.IsNullOrWhiteSpace(queueName))
        {
            throw new ArgumentException(
                "The default dead letter queue name must be a non-empty value. Use DisableDeadLetterQueueing() on an endpoint to disable dead letter queueing.",
                nameof(queueName));
        }

        Transport.DefaultDeadLetterQueueName = Transport.SanitizeIdentifier(queueName.Trim());

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
        buildControlQueue();

        return this;
    }

    // The control queue is built here and now rather than in tryBuildSystemEndpoints, because
    // Options.Transports.NodeControlEndpoint is read by the message stores and the node agent
    // (MessageDatabase, WolverineNode) before the transports ever initialize.
    private void buildControlQueue()
    {
        // In Solo mode the assigned node number is always 1 (#3188); key the per-node control queue
        // on the unique node id so multiple Solo hosts on one namespace don't collide. See #3189.
        var controlNode = Options.Durability.Mode == DurabilityMode.Solo
            ? Options.UniqueNodeId.ToString("N")
            : Options.Durability.AssignedNodeNumber.ToString();
        var queueName = Transport.PrefixSystemQueueName("wolverine.control." + controlNode);

        // SystemQueuePrefix() can be called after EnableWolverineControlQueues(), in which case the
        // control queue already exists under the unprefixed name. Take it back out so the transport
        // doesn't go on to provision and listen to a queue nothing points at anymore.
        if (Transport.ControlQueue != null && Transport.ControlQueue.QueueName != queueName)
        {
            Transport.Queues.Remove(Transport.ControlQueue.QueueName);
        }

        var queue = Transport.Queues[queueName];

        queue.Options.AutoDeleteOnIdle = 5.Minutes();
        queue.Mode = EndpointMode.BufferedInMemory;
        queue.IsListener = true;
        queue.EndpointName = "Control";
        queue.IsUsedForReplies = true;
        queue.Role = EndpointRole.System;

        Options.Transports.NodeControlEndpoint = queue;
        Transport.ControlQueue = queue;
    }
}