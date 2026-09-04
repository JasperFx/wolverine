# Using Azure Service Bus

::: tip
Wolverine.AzureServiceBus is able to support inline, buffered, or durable endpoints.
:::

Wolverine supports [Azure Service Bus](https://learn.microsoft.com/en-us/azure/service-bus-messaging/service-bus-messaging-overview) as a messaging transport through the WolverineFx.AzureServiceBus nuget.

## Connecting to the Broker

After referencing the Nuget package, the next step to using Azure Service Bus within your Wolverine
application is to connect to the service broker using the `UseAzureServiceBus()` extension
method as shown below in this basic usage:

<!-- snippet: sample_basic_connection_to_azure_service_bus -->
<a id='snippet-sample_basic_connection_to_azure_service_bus'></a>
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

        // Let Wolverine try to initialize any missing queues
        // on the first usage at runtime
        .AutoProvision()

        // Direct Wolverine to purge all queues on application startup.
        // This is probably only helpful for testing
        .AutoPurgeOnStartup();

    // Or if you need some further specification...
    opts.UseAzureServiceBus(azureServiceBusConnectionString,
        azure => { azure.RetryOptions.Mode = ServiceBusRetryMode.Exponential; });
});

using var host = builder.Build();
await host.StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Azure/Wolverine.AzureServiceBus.Tests/DocumentationSamples.cs#L14-L43' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_basic_connection_to_azure_service_bus' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The advanced configuration for the broker is the [ServiceBusClientOptions](https://learn.microsoft.com/en-us/dotnet/api/azure.messaging.servicebus.servicebusclientoptions?view=azure-dotnet) class from the Azure.Messaging.ServiceBus
library. 

For security purposes, there are overloads of `UseAzureServiceBus()` that will also accept and opt into Azure Service Bus authentication with:

1. [TokenCredential](https://learn.microsoft.com/en-us/dotnet/api/azure.core.tokencredential?view=azure-dotnet)
2. [AzureNamedKeyCredential](https://learn.microsoft.com/en-us/dotnet/api/azure.azurenamedkeycredential?view=azure-dotnet)
3. [AzureSasCredential](https://learn.microsoft.com/en-us/dotnet/api/azure.azuresascredential?view=azure-dotnet)

## Aspire Integration

The cleanest way to integrate Wolverine with .NET Aspire for Azure Service Bus is via `TokenCredential`, typically
`DefaultAzureCredential` from `Azure.Identity`. Aspire injects the Service Bus namespace via the
`SERVICEBUS_URI` environment variable (or similar), and you pass it with the credential:

**AppHost** (`Aspire.Hosting.Azure.ServiceBus` NuGet):
```csharp
var serviceBus = builder.AddAzureServiceBus("servicebus");

builder.AddProject<Projects.MyWorker>("worker")
    .WithReference(serviceBus)
    .WaitFor(serviceBus);
```

**Service project** (`Aspire.Azure.Messaging.ServiceBus` client NuGet registers `ServiceBusClient` in DI):
```csharp
using Azure.Identity;

// Option 1: Use Aspire.Azure.Messaging.ServiceBus to register ServiceBusClient in DI,
// then read the namespace from configuration:
var fullyQualifiedNamespace = builder.Configuration["Azure:ServiceBus:FullyQualifiedNamespace"]
    ?? builder.Configuration.GetConnectionString("servicebus")!;

builder.UseWolverine(opts =>
{
    opts.UseAzureServiceBus(fullyQualifiedNamespace, new DefaultAzureCredential())
        // AutoProvision creates missing queues, topics, and subscriptions at startup
        .AutoProvision();

    opts.ListenToAzureServiceBusQueue("my-queue");
    opts.PublishMessage<MyMessage>().ToAzureServiceBusQueue("my-queue");
});
```

When using the [Azure Service Bus emulator](/guide/messaging/transports/azureservicebus/emulator) for local development or testing,
use `UseAzureServiceBusEmulator()` instead. It connects to the emulator's messaging (AMQP) port *and* its separate management (HTTP)
port for you:

<!-- snippet: sample_using_azure_service_bus_emulator -->
<a id='snippet-sample_using_azure_service_bus_emulator'></a>
```cs
var builder = Host.CreateApplicationBuilder();
builder.UseWolverine(opts =>
{
    // Connect to a locally running Azure Service Bus emulator using the
    // standard emulator ports (AMQP on 5672, management on 5300)
    opts.UseAzureServiceBusEmulator()

        // The emulator starts out empty, so let Wolverine build
        // any queues, topics, or subscriptions it needs
        .AutoProvision()
        .AutoPurgeOnStartup();

    opts.ListenToAzureServiceBusQueue("my-queue");
    opts.PublishAllMessages().ToAzureServiceBusQueue("my-queue");
});

using var host = builder.Build();
await host.StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Azure/Wolverine.AzureServiceBus.Tests/DocumentationSamples.cs#L48-L69' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_azure_service_bus_emulator' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

See [Using the Azure Service Bus Emulator](/guide/messaging/transports/azureservicebus/emulator) for the Docker Compose setup, the
overload that takes explicit connection strings, and the opt in namespace cleanup.

## Request/Reply

[Request/reply](https://www.enterpriseintegrationpatterns.com/patterns/messaging/RequestReply.html) mechanics (`IMessageBus.InvokeAsync<T>()`) are possible with the Azure Service Bus transport *if* Wolverine has the ability to auto-provision
a specific response queue for each node. That queue would be named like `wolverine.response.[service name].[application node id]` if you happen
to notice that in the Azure Portal — or `[prefix].wolverine.response.[service name].[application node id]` if you opted into
[prefixing the system queues](#prefixing-system-queues).

And also see the next section. 

## Wolverine Control Queues

You can opt into using temporary Azure Service Bus queues for intra-node communication
that Wolverine needs for leader election and background worker distribution. Using Azure
Service Bus for this feature is more efficient than the built in database control
queues that Wolverine uses otherwise, and is necessary for message storage options like
RavenDb that do not have a built in control queue mechanism.

<!-- snippet: sample_enabling_azure_service_bus_control_queues -->
<a id='snippet-sample_enabling_azure_service_bus_control_queues'></a>
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
        .AutoProvision()
        
        // This enables Wolverine to use temporary Azure Service Bus
        // queues created at runtime for communication between
        // Wolverine nodes
        .EnableWolverineControlQueues();

});
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Azure/Wolverine.AzureServiceBus.Tests/DocumentationSamples.cs#L499-L521' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_enabling_azure_service_bus_control_queues' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Disabling System Queues

If your application will not have permissions to create temporary queues in Azure Service Bus, you will probably want
to disable system queues to avoid having some annoying error messages popping up. That's easy enough though:

<!-- snippet: sample_disable_system_queues_in_azure_service_bus -->
<a id='snippet-sample_disable_system_queues_in_azure_service_bus'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.UseAzureServiceBus("some connection string")
            .AutoProvision().AutoPurgeOnStartup()
            .SystemQueuesAreEnabled(false);

        opts.ListenToAzureServiceBusQueue("send_and_receive");

        opts.PublishAllMessages().ToAzureServiceBusQueue("send_and_receive");
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Azure/Wolverine.AzureServiceBus.Tests/DocumentationSamples.cs#L242-L256' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_disable_system_queues_in_azure_service_bus' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Prefixing System Queues <Badge type="tip" text="6.34" />

Wolverine creates a handful of queues for its own use, and by default their names all start with the `wolverine`
token:

| Queue | Default name |
|-------|--------------|
| Response queue (request/reply) | `wolverine.response.[service name].[node]` |
| Retry queue | `wolverine.retries.[service name]` |
| Node control queue | `wolverine.control.[node]` |
| Dead letter queue | `wolverine-dead-letter-queue` |

Only the first two carry the service name. The control queue and the dead letter queue do not, so several unrelated
applications sharing one Azure Service Bus namespace will happily collide on them — and if you turned on
[dead letter queue recovery](/guide/messaging/transports/azureservicebus/deadletterqueues), one application's recovery
listener will merrily drain another application's dead letters into its own storage.

`SystemQueuePrefix()` prepends a prefix of your choosing to all four of those names, so each application owns its own
set:

<!-- snippet: sample_asb_system_queue_prefix -->
<a id='snippet-sample_asb_system_queue_prefix'></a>
```cs
var builder = Host.CreateApplicationBuilder();
builder.UseWolverine(opts =>
{
    var azureServiceBusConnectionString = builder
        .Configuration
        .GetConnectionString("azure-service-bus")!;

    opts.ServiceName = "Orders";

    opts.UseAzureServiceBus(azureServiceBusConnectionString)
        .AutoProvision()

        // Every queue that Wolverine creates for its own use is now
        // prefixed with "my-project.", so this application can share an
        // Azure Service Bus namespace with unrelated applications:
        //
        //   my-project.wolverine.response.Orders.{node}
        //   my-project.wolverine.retries.orders
        //   my-project.wolverine.control.{node}
        //   my-project.wolverine-dead-letter-queue
        .SystemQueuePrefix("my-project")

        .EnableWolverineControlQueues();

    // Application queue names are untouched, so cooperating applications
    // still address exactly the same queues
    opts.ListenToAzureServiceBusQueue("orders");
});

using var host = builder.Build();
await host.StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Azure/Wolverine.AzureServiceBus.Tests/DocumentationSamples.cs#L944-L978' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_asb_system_queue_prefix' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The prefix is joined with the Azure Service Bus identifier delimiter (`.`), and the `wolverine` token is kept so the
queues are still recognizable as Wolverine's in the Azure Portal. Without a prefix — the default — every one of these
names is exactly what it has always been, so this is safe to leave alone in an existing application.

::: tip
This is a different thing than [`PrefixIdentifiers()`](/guide/messaging/transports/azureservicebus/object-management#identifier-prefixing-for-shared-brokers),
which renames your *application* queues, topics, and subscriptions and deliberately leaves Wolverine's system queues
alone. That distinction matters: two cooperating applications that send messages to each other have to keep addressing
the same application queue names, so `PrefixIdentifiers()` is not usable there, while each still needs its own control,
response, retry, and dead letter queues. The two can also be combined when you want full isolation — say, per developer
against a shared namespace.
:::

A dead letter queue that you name yourself is taken as fully qualified and is never prefixed. That applies to both
`ConfigureDeadLetterQueue("x")` on a single endpoint and
[`DefaultDeadLetterQueueName("x")`](/guide/messaging/transports/azureservicebus/deadletterqueues#overriding-the-default-dead-letter-queue-name)
across the transport.

Order does not matter with respect to `EnableWolverineControlQueues()`. The control queue has to be built eagerly at
configuration time, because Wolverine's message stores and node agent read it long before the transports initialize, so
calling `SystemQueuePrefix()` afterwards rebuilds the control queue under the prefixed name and discards the unprefixed
one.

## Connecting To Multiple Namespaces <Badge type="tip" text="5.0" />

Wolverine supports the "named broker" feature to connect to multiple Azure Service Bus namespaces from one application:

<!-- snippet: sample_using_named_azure_service_bus_broker -->
<a id='snippet-sample_using_named_azure_service_bus_broker'></a>
```cs
var builder = Host.CreateApplicationBuilder();
builder.UseWolverine(opts =>
{
    var connectionString1 = builder.Configuration.GetConnectionString("azureservicebus1")!;
    opts.AddNamedAzureServiceBusBroker(new BrokerName("one"), connectionString1);

    var connectionString2 = builder.Configuration.GetConnectionString("azureservicebus2")!;
    opts.AddNamedAzureServiceBusBroker(new BrokerName("two"), connectionString2);

    opts.PublishAllMessages().ToAzureServiceBusQueueOnNamedBroker(new BrokerName("one"), "queue1");

    opts.ListenToAzureServiceBusQueueOnNamedBroker(new BrokerName("two"), "incoming");

    opts.ListenToAzureServiceBusSubscriptionOnNamedBroker(new BrokerName("two"), "subscription1");
});
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Azure/Wolverine.AzureServiceBus.Tests/end_to_end_with_named_broker.cs#L26-L43' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_named_azure_service_bus_broker' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

`UseConventionalRouting()` and `UseTopicAndSubscriptionConventionalRouting()` can be chained off a named broker just like any
other endpoint configuration, and the convention applies to that broker rather than the default one.

The named broker methods take only the *messaging* (AMQP) connection string. That is all a real Azure Service Bus namespace
needs, but if the management (HTTP) endpoint is separate -- as it is against the
[emulator](/guide/messaging/transports/azureservicebus/emulator#named-brokers-and-the-management-connection-string) -- it has to
be set on the named transport explicitly, or anything that talks to the management API will fail at startup.

## Global Partitioning

Azure Service Bus queues can be used as the external transport for [global partitioned messaging](/guide/messaging/partitioning#global-partitioning). This creates a set of sharded Azure Service Bus queues with companion local queues for sequential processing across a multi-node cluster.

Use `UseShardedAzureServiceBusQueues()` within a `GlobalPartitioned()` configuration:

```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.UseAzureServiceBus(azureServiceBusConnectionString).AutoProvision();

        opts.MessagePartitioning.ByMessage<IMyMessage>(x => x.GroupId);

        opts.MessagePartitioning.GlobalPartitioned(topology =>
        {
            // Creates 4 sharded Azure Service Bus queues named "orders1" through "orders4"
            // with matching companion local queues for sequential processing
            topology.UseShardedAzureServiceBusQueues("orders", 4);
            topology.MessagesImplementing<IMyMessage>();
        });
    }).StartAsync();
```

This creates Azure Service Bus queues named `orders1` through `orders4` with companion local queues `global-orders1` through `global-orders4`. Messages are routed to the correct shard based on their group id, and Wolverine handles the coordination between nodes automatically.

::: tip
Azure Service Bus also has a native, broker-side alternative to this feature. [Session identifiers](/guide/messaging/transports/azureservicebus/session-identifiers) provide strictly ordered processing per session id with a single queue and no sharded topology. Consider sessions first if you are exclusively on Azure Service Bus; global partitioning is the transport-agnostic option that behaves identically across every supported broker.
:::

## URI reference

The `AzureServiceBusEndpointUri` helper class builds canonical endpoint URIs:

| URI form | Helper call |
|---|---|
| `asb://queue/{name}` | `AzureServiceBusEndpointUri.Queue("name")` |
| `asb://topic/{name}` | `AzureServiceBusEndpointUri.Topic("name")` |
| `asb://topic/{topic}/{subscription}` | `AzureServiceBusEndpointUri.Subscription("topic", "sub")` |

```csharp
using Wolverine.AzureServiceBus;

var uri = AzureServiceBusEndpointUri.Subscription("events", "audit");
```


