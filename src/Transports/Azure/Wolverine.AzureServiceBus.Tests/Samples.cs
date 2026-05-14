using Azure.Messaging.ServiceBus;
using JasperFx.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using JasperFx.Resources;
using Wolverine.ComplianceTests.Compliance;
using Wolverine.Transports.Sending;
using Wolverine.Util;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests;

public class Samples
{
    public static async Task configure_topics()
    {
        #region sample_using_azure_service_bus_subscriptions_and_topics
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAzureServiceBus("some connection string")

                    // If this is part of your configuration, Wolverine will try to create
                    // any missing topics or subscriptions in the configuration at application
                    // start up time
                    .AutoProvision();

                // Publish to a topic
                opts.PublishMessage<Message1>().ToAzureServiceBusTopic("topic1")

                    // Option to configure how the topic would be configured if
                    // built by Wolverine
                    .ConfigureTopic(topic =>
                    {
                        topic.MaxSizeInMegabytes = 100;
                    });


                opts.ListenToAzureServiceBusSubscription("subscription1", subscription =>
                    {
                        // Optionally alter how the subscription is created or configured in Azure Service Bus
                        subscription.DefaultMessageTimeToLive = 5.Minutes();
                    })
                    .FromTopic("topic1", topic =>
                    {
                        // Optionally alter how the topic is created in Azure Service Bus
                        topic.DefaultMessageTimeToLive = 5.Minutes();
                    });
            }).StartAsync();

        #endregion
    }

    public static async Task configure_resources()
    {
        #region sample_resource_setup_with_azure_service_bus
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAzureServiceBus("some connection string");

                // Make sure that all known resources like
                // the Azure Service Bus queues, topics, and subscriptions
                // configured for this application exist at application start up
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        #endregion
    }

    public static async Task configure_auto_provision()
    {
        #region sample_auto_provision_with_azure_service_bus
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAzureServiceBus("some connection string")

                    // Wolverine will build missing queues, topics, and subscriptions
                    // as necessary at runtime
                    .AutoProvision();
            }).StartAsync();

        #endregion
    }

    public static async Task configure_auto_purge()
    {
        #region sample_auto_purge_with_azure_service_bus
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAzureServiceBus("some connection string")
                    .AutoPurgeOnStartup();
            }).StartAsync();

        #endregion
    }

    public static async Task configure_custom_mappers()
    {
        #region sample_configuring_custom_envelope_mapper_for_azure_service_bus
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAzureServiceBus("some connection string")
                    .UseConventionalRouting()

                    .ConfigureListeners(l => l.InteropWith(new CustomAzureServiceBusMapper()))

                    .ConfigureSenders(s => s.InteropWith(new CustomAzureServiceBusMapper()));
            }).StartAsync();

        #endregion
    }
    
public class multi_tenanted_brokers
{
    //[Fact]
    public void show_bootstrapping()
    {
        #region sample_configuring_azure_service_bus_for_multi_tenancy
        var builder = Host.CreateApplicationBuilder();

        builder.UseWolverine(opts =>
        {
            // One way or another, you're probably pulling the Azure Service Bus
            // connection string out of configuration
            var azureServiceBusConnectionString = builder
                .Configuration
                .GetConnectionString("azure-service-bus")!;

            // Connect to the broker in the simplest possible way
            opts.UseAzureServiceBus(azureServiceBusConnectionString)

                // This is the default, if there is no tenant id on an outgoing message,
                // use the default broker
                .TenantIdBehavior(TenantedIdBehavior.FallbackToDefault)

                // Or tell Wolverine instead to just quietly ignore messages sent
                // to unrecognized tenant ids
                .TenantIdBehavior(TenantedIdBehavior.IgnoreUnknownTenants)

                // Or be draconian and make Wolverine assert and throw an exception
                // if an outgoing message does not have a tenant id
                .TenantIdBehavior(TenantedIdBehavior.TenantIdRequired)

                // Add new tenants by registering the tenant id and a separate fully qualified namespace
                // to a different Azure Service Bus connection
                .AddTenantByNamespace("one", builder.Configuration.GetValue<string>("asb_ns_one")!)
                .AddTenantByNamespace("two", builder.Configuration.GetValue<string>("asb_ns_two")!)
                .AddTenantByNamespace("three", builder.Configuration.GetValue<string>("asb_ns_three")!)

                // OR, instead, add tenants by registering the tenant id and a separate connection string
                // to a different Azure Service Bus connection
                .AddTenantByConnectionString("four", builder.Configuration.GetConnectionString("asb_four")!)
                .AddTenantByConnectionString("five", builder.Configuration.GetConnectionString("asb_five")!)
                .AddTenantByConnectionString("six", builder.Configuration.GetConnectionString("asb_six")!);
            
            // This Wolverine application would be listening to a queue
            // named "incoming" on all Azure Service Bus connections, including the default
            opts.ListenToAzureServiceBusQueue("incoming");

            // This Wolverine application would listen to a single queue
            // at the default connection regardless of tenant
            opts.ListenToAzureServiceBusQueue("incoming_global")
                .GlobalListener();
            
            // Likewise, you can override the queue, subscription, and topic behavior
            // to be "global" for all tenants with this syntax:
            opts.PublishMessage<Message1>()
                .ToAzureServiceBusQueue("message1")
                .GlobalSender();

            opts.PublishMessage<Message2>()
                .ToAzureServiceBusTopic("message2")
                .GlobalSender();
        });

        #endregion
    }
}
}

#region sample_custom_azure_service_bus_mapper
public class CustomAzureServiceBusMapper : IAzureServiceBusEnvelopeMapper
{
    public void MapEnvelopeToOutgoing(Envelope envelope, ServiceBusMessage outgoing)
    {
        outgoing.Body = new BinaryData(envelope.Data!);
        if (envelope.DeliverWithin != null)
        {
            outgoing.TimeToLive = envelope.DeliverWithin.Value;
        }
    }

    public void MapIncomingToEnvelope(Envelope envelope, ServiceBusReceivedMessage incoming)
    {
        envelope.Data = incoming.Body.ToArray();

        // You will have to help Wolverine out by either telling Wolverine
        // what the message type is, or by reading the actual message object,
        // or by telling Wolverine separately what the default message type
        // is for a listening endpoint
        envelope.MessageType = typeof(Message1).ToMessageTypeName();
    }
}

#endregion

// ---------------------------------------------------------------------------
// Topic-per-tenant routing recipe (docs reference for #2630). Lives here so
// the docs snippets compile against the real public Wolverine API surface,
// not as a shipping helper in the WolverineFx.AzureServiceBus package — the
// per-application semantics around tenant lookup, missing-tenant handling,
// and topic naming are user-owned. Originally contributed in #2630.

#region sample_topic_per_tenant_route
internal sealed class TopicPerTenantRoute(IReadOnlyDictionary<string, AzureServiceBusTopic> topicsByTenant)
    : IMessageRouteSource, IMessageRoute, IEndpointSource
{
    private ImHashMap<(Type messageType, string tenantId), MessageRoute> _routes
        = ImHashMap<(Type, string), MessageRoute>.Empty;

    // Non-additive: this is the canonical route for every user message type.
    // The default LocalRouting / ExplicitRouting sources still take precedence
    // for framework-internal messages because of the IsInternalMessage filter
    // in FindRoutes below.
    public bool IsAdditive => false;

    public IEnumerable<IMessageRoute> FindRoutes(Type messageType, IWolverineRuntime runtime)
    {
        if (messageType.CanBeCastTo<IInternalMessage>()) yield break;
        if (messageType.CanBeCastTo<IAgentCommand>()) yield break;
        if (messageType.CanBeCastTo<INotToBeRouted>()) yield break;
        yield return this;
    }

    // Surfaces the per-tenant topics so endpoint-level policies
    // (UseDurableOutboxOnAllSendingEndpoints, OpenTelemetry registration, etc.)
    // can discover them via the IEndpointSource seam.
    public IEnumerable<Endpoint> ActiveEndpoints() => topicsByTenant.Values;

    public Envelope CreateForSending(
        object message,
        DeliveryOptions? options,
        ISendingAgent localDurableQueue,
        WolverineRuntime runtime,
        string? topicName)
    {
        var route = ResolveRoute(message.GetType(), options?.TenantId, runtime);
        return route.CreateForSending(message, options, localDurableQueue, runtime, topicName);
    }

    public MessageSubscriptionDescriptor Describe() => new()
    {
        ContentType = "application/json",
        Description = $"Tenant-aware Azure Service Bus topic routing across {topicsByTenant.Count} tenants",
        Endpoint = topicsByTenant.Values.First().Uri
    };

    private MessageRoute ResolveRoute(Type messageType, string? tenantId, IWolverineRuntime runtime)
    {
        if (tenantId.IsEmpty())
        {
            throw new InvalidOperationException(
                $"Cannot publish a message of type {messageType.FullNameInCode()} without a TenantId; " +
                "topic-per-tenant routing is configured.");
        }

        if (!topicsByTenant.TryGetValue(tenantId, out var topic))
        {
            throw new InvalidOperationException(
                $"Unknown tenant ID '{tenantId}' for message of type {messageType.FullNameInCode()}; " +
                "no topic registered for this tenant.");
        }

        return GetOrBuildRoute(messageType, tenantId, topic, runtime);
    }

    private MessageRoute GetOrBuildRoute(
        Type messageType,
        string tenantId,
        AzureServiceBusTopic topic,
        IWolverineRuntime runtime)
    {
        var key = (messageType, tenantId);
        if (_routes.TryFind(key, out var route)) return route;

        route = new MessageRoute(messageType, topic, runtime);
        _routes = _routes.AddOrUpdate(key, route);
        return route;
    }
}

#endregion

#region sample_topic_per_tenant_route_extension
public static class TopicPerTenantWolverineOptionsExtensions
{
    /// <summary>
    /// Route every outgoing user message to a per-tenant Azure Service Bus topic
    /// inside a single namespace. The tenant id is read from
    /// <see cref="DeliveryOptions.TenantId"/> at publish time; an unknown tenant
    /// or a missing tenant id raises <see cref="InvalidOperationException"/>.
    /// </summary>
    /// <param name="opts">The Wolverine options.</param>
    /// <param name="tenantIds">The set of known tenant ids.</param>
    /// <param name="topicNameForTenant">Maps a tenant id to its raw topic name.
    /// The transport's identifier prefix and sanitization rules are applied automatically.</param>
    public static WolverineOptions RouteByTenantToAzureServiceBusTopics(
        this WolverineOptions opts,
        IEnumerable<string> tenantIds,
        Func<string, string> topicNameForTenant)
    {
        var transport = opts.Transports.GetOrCreate<AzureServiceBusTransport>();

        var topicsByTenant = new Dictionary<string, AzureServiceBusTopic>(StringComparer.Ordinal);
        foreach (var id in tenantIds)
        {
            var rawName = topicNameForTenant(id);
            var name = transport.MaybeCorrectName(rawName);
            var topic = transport.Topics[name];
            topic.EndpointName = rawName;
            topicsByTenant[id] = topic;
        }

        if (topicsByTenant.Count == 0)
        {
            throw new ArgumentException("At least one tenant ID is required.", nameof(tenantIds));
        }

        opts.PublishWithMessageRoutingSource(new TopicPerTenantRoute(topicsByTenant));

        return opts;
    }
}

#endregion

public static class TopicPerTenantUsageSample
{
    public static async Task using_topic_per_tenant_route()
    {
        #region sample_using_topic_per_tenant_route
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // The single Azure Service Bus namespace that hosts every
                // tenant's topic.
                opts.UseAzureServiceBus("Endpoint=sb://saas.servicebus.windows.net/;...");

                // Wire the per-tenant routing source. Tenant ids and topic
                // names typically come from configuration; the closure stays
                // free to do whatever lookup makes sense for the application
                // (configuration sections, a tenant-catalog table, etc.).
                var tenantIds = new[] { "tenant-a", "tenant-b", "tenant-c" };
                opts.RouteByTenantToAzureServiceBusTopics(
                    tenantIds,
                    tenantId => $"messages-{tenantId}");
            }).StartAsync();

        // At publish time, the tenant id is supplied via DeliveryOptions.
        // The route source resolves the correct per-tenant topic before the
        // message ever reaches the transport.
        var bus = host.Services.GetRequiredService<IMessageBus>();
        await bus.PublishAsync(
            new TenantBoundMessage("hello"),
            new DeliveryOptions { TenantId = "tenant-b" });
        #endregion
    }
}

public sealed record TenantBoundMessage(string Payload);