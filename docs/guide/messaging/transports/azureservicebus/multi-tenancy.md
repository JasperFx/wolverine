# Multi-Tenancy with Azure Service Bus <Badge type="tip" text="3.4" />

::: tip
For a holistic overview of multi-tenancy across all of Wolverine, see the [Multi-Tenancy Tutorial](/tutorials/multi-tenancy).
For more context on this feature, see the blog post [Message Broker per Tenant with Wolverine](https://jeremydmiller.com/2024/12/02/message-broker-per-tenant-with-wolverine/).
:::

Let's take a trip to the world of IoT where you might very well build a single cloud hosted service that needs
to communicate via Rabbit MQ with devices at your customers sites. You'd preferably like to keep traffic separate
so that one customer never accidentally receives information from another customer. In this case, Wolverine now
lets you register separate Rabbit MQ brokers -- or at least separate virtual hosts within a single Rabbit MQ broker --
for each tenant.

::: info
Definitely see [Multi-Tenancy with Wolverine](/guide/handlers/multi-tenancy) for more information about how
Wolverine tracks the tenant id across messages. 
:::

Let's just jump straight into a simple example of the configuration:

<!-- snippet: sample_configuring_azure_service_bus_for_multi_tenancy -->
<a id='snippet-sample_configuring_azure_service_bus_for_multi_tenancy'></a>
```cs
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
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Azure/Wolverine.AzureServiceBus.Tests/Samples.cs#L122-L180' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_configuring_azure_service_bus_for_multi_tenancy' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

::: warning
Wolverine has no way of creating new Azure Service Bus namespaces for you
:::

In the code sample above, I'm setting up the Azure Service Bus transport to "know" that there are multiple tenants
with separate Azure Service Bus fully qualified namespaces. 

::: tip
Note that Wolverine uses the credentials specified for the default Azure Service
Bus connection for all tenant specific connections
:::

At runtime, if we send a message like so:

<!-- snippet: sample_send_message_to_specific_tenant -->
<a id='snippet-sample_send_message_to_specific_tenant'></a>
```cs
public static async Task send_message_to_specific_tenant(IMessageBus bus)
{
    // Send a message tagged to a specific tenant id
    await bus.PublishAsync(new Message1(), new DeliveryOptions { TenantId = "two" });
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/RabbitMQ/Wolverine.RabbitMQ.Tests/multi_tenancy_through_virtual_hosts.cs#L326-L333' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_send_message_to_specific_tenant' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

In the case above, in the Wolverine internals, it:

1. Routes the message to a Azure Service Bus queue named "outgoing"
2. Within the sender for that queue, Wolverine sees that `TenantId == "two"`, so it sends the message to the "outgoing" queue
   on the Azure Service Bus connection that we specified for the "two" tenant id.

Likewise, see the listening set up against the "incoming" queue above. At runtime, this Wolverine application will be
listening to a queue named "incoming" on the default Azure Service Bus namespace and a separate queue named "incoming" on the separate
fully qualified namespaces for the known tenants. When a message is received at any of these queues, it's tagged with the 
`TenantId` that's appropriate for each separate tenant-specific listening endpoint. That helps Wolverine also track
tenant specific operations (with Marten maybe?) and tracks the tenant id across any outgoing messages or responses as well.

## Single Namespace, Topic-per-Tenant Routing <Badge type="tip" text="6.0" />

The configuration above gives every tenant its own Azure Service Bus *namespace*. That topology is the right answer when
isolation, throttling, or chargeback have to live at the broker boundary.

A second, lighter-weight topology is common in SaaS deployments where the application owns a single Azure Service Bus
namespace and gives every tenant a dedicated *topic* inside it. Tenant isolation lives at the topic level; one connection
string, one namespace-level RBAC policy, one set of metrics. The tradeoff is that broker-level concerns (per-tenant
throttling, namespace-scoped credentials) all collapse onto the shared namespace — pick this topology when that's
acceptable.

Wolverine doesn't ship a built-in helper for topic-per-tenant routing because the right answer to *how* tenant ids map to
topic names, *what* to do about unknown tenants, and *when* the catalog of tenants is loaded all depend on the
application. The recipe below is a small `IMessageRouteSource` implementation plus a `WolverineOptions` extension method
that any application can drop in and adapt.

### The route source

`TopicPerTenantRoute` resolves the per-tenant `AzureServiceBusTopic` at publish time using the
`DeliveryOptions.TenantId` carried by the outgoing envelope. Wolverine's per-message-type router asks
`IMessageRouteSource.FindRoutes` exactly once per message type (the result is cached) — so the actual per-tenant
resolution happens inside `CreateForSending`, where the `TenantId` is known.

<!-- snippet: sample_topic_per_tenant_route -->
<a id='snippet-sample_topic_per_tenant_route'></a>
```cs
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
```
<!-- endSnippet -->

### The extension method

The extension wraps the route-source registration and walks the configured tenant list to materialize the per-tenant
`AzureServiceBusTopic` endpoints. `MaybeCorrectName` applies the transport's identifier prefix and Service Bus naming
rules so the actual broker topic names match what auto-provisioning would create.

<!-- snippet: sample_topic_per_tenant_route_extension -->
<a id='snippet-sample_topic_per_tenant_route_extension'></a>
```cs
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
```
<!-- endSnippet -->

### Wiring it up

<!-- snippet: sample_using_topic_per_tenant_route -->
<a id='snippet-sample_using_topic_per_tenant_route'></a>
```cs
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
```
<!-- endSnippet -->

### Behavior notes

- The route source is **non-additive** — it short-circuits the default routing sources for any user message type. Local
  handlers, framework-internal messages (`IInternalMessage`, `IAgentCommand`, `INotToBeRouted`), and explicit per-message
  `PublishMessage<T>().To*()` configuration still resolve through their own paths because of the filters in `FindRoutes`.
  If your application needs both topic-per-tenant routing for some messages and explicit routing for others, tweak the
  filter set in `FindRoutes` to skip the explicitly-routed message types.
- A missing or empty `DeliveryOptions.TenantId` and an unknown tenant id both raise `InvalidOperationException` at
  publish time. Decide whether your application wants exception semantics or silent drop, and adapt `ResolveRoute`
  accordingly.
- The per-tenant topic catalog is captured at startup. If new tenants need to be onboarded without a host restart, swap
  the constructor's `IReadOnlyDictionary<string, AzureServiceBusTopic>` for a refreshable lookup (an
  `IOptionsMonitor<...>`-backed view, or an explicit `RegisterTenant(string, string)` method that mutates an internal
  `ImHashMap`).

### Limitations vs. the broker-per-tenant topology

The recipe above shares the **same Azure Service Bus connection string** across every tenant — that's the whole point of
topic-per-tenant routing. If different tenants need different connection strings or fully-qualified namespaces, use the
broker-per-tenant configuration above (`AddTenantByConnectionString` / `AddTenantByNamespace`) instead. The two
topologies don't compose; pick one.


