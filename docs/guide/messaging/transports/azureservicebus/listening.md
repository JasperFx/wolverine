# Listening for Messages

::: warning
The Azure Service Bus transport uses batching to both send and receive messages. As such,
the listeners or senders can only be configured to use buffered or durable mechanics. I.e., there
is no current option for inline senders or listeners.
:::

You can configure explicit queue listening with this syntax:

<!-- snippet: sample_configuring_an_azure_service_bus_listener -->
<a id='snippet-sample_configuring_an_azure_service_bus_listener'></a>
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
    opts.UseAzureServiceBus(azureServiceBusConnectionString).AutoProvision();

    opts.ListenToAzureServiceBusQueue("incoming")

        // Customize how many messages to retrieve at one time
        .MaximumMessagesToReceive(100)

        // Customize how long the listener will wait for more messages before
        // processing a batch
        .MaximumWaitTime(3.Seconds())

        // Add a circuit breaker for systematic failures
        .CircuitBreaker()

        // And all the normal Wolverine options you'd expect
        .BufferedInMemory();
});

using var host = builder.Build();
await host.StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Azure/Wolverine.AzureServiceBus.Tests/DocumentationSamples.cs#L244-L276' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_configuring_an_azure_service_bus_listener' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Configuring the ServiceBusProcessor

When a listener runs [inline](/guide/messaging/endpoints.html#endpoint-types) (`ProcessInline()`), Wolverine
creates an Azure Service Bus `ServiceBusProcessor` for the queue or subscription. You can customize the
underlying `ServiceBusProcessorOptions` with `ConfigureProcessor()` on either a queue or a subscription
listener.

The most common reason to reach for this is a long-running inline handler. The Azure SDK only renews a
message's lock for five minutes by default (`MaxAutoLockRenewalDuration`). Any inline handler that runs
longer than that loses the lock, so the completion acknowledgement silently fails and Azure Service Bus
redelivers the message — resulting in duplicate work until the message is finally dead-lettered. Raising
`MaxAutoLockRenewalDuration` keeps the lock alive for the length of your handler:

<!-- snippet: sample_configuring_azure_service_bus_processor_options -->
<a id='snippet-sample_configuring_azure_service_bus_processor_options'></a>
```cs
var builder = Host.CreateApplicationBuilder();
builder.UseWolverine(opts =>
{
    // One way or another, you're probably pulling the Azure Service Bus
    // connection string out of configuration
    var azureServiceBusConnectionString = builder
        .Configuration
        .GetConnectionString("azure-service-bus")!;

    opts.UseAzureServiceBus(azureServiceBusConnectionString).AutoProvision();

    opts.ListenToAzureServiceBusQueue("incoming")

        // Inline listeners create an Azure Service Bus ServiceBusProcessor. By default the
        // Azure SDK only renews the message lock for five minutes, so an inline handler that
        // runs longer than that loses its lock and the message is redelivered. Raise the
        // renewal window here so long-running inline handlers keep their lock.
        .ConfigureProcessor(processorOptions =>
        {
            processorOptions.MaxAutoLockRenewalDuration = TimeSpan.FromMinutes(30);
        })

        // Run the handler inline against the ServiceBusProcessor
        .ProcessInline();
});

using var host = builder.Build();
await host.StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Azure/Wolverine.AzureServiceBus.Tests/DocumentationSamples.cs#L281-L311' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_configuring_azure_service_bus_processor_options' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

::: warning
Wolverine's inline listener acknowledges, defers, and dead letters messages against the message lock, so it
requires the peek-lock receive model. The `ReceiveMode` you set in `ConfigureProcessor()` is therefore
ignored and always re-asserted to `ServiceBusReceiveMode.PeekLock`. All other `ServiceBusProcessorOptions`
properties are honored. Session-based listeners (`RequireSessions()`) do not use a `ServiceBusProcessor` and
are not affected by this option.
:::

## Client-Side Prefetch

For high-throughput endpoints, the single cheapest knob in this transport is the Azure Service Bus
client's *prefetch* buffer. By default prefetch is disabled (`PrefetchCount = 0`), so every
`ReceiveMessagesAsync()` round trip has to go all the way to the broker before any message can be
handled. Setting a positive `PrefetchCount` has the Azure Service Bus client eagerly stream messages
into a local buffer in the background, so the listener's receive calls are typically satisfied
immediately from memory.

You can set a prefetch count on any queue or subscription listener, and/or set a transport-wide
default that every Azure Service Bus listening endpoint inherits unless it overrides the value
itself:

<!-- snippet: sample_configuring_azure_service_bus_prefetch_count -->
<a id='snippet-sample_configuring_azure_service_bus_prefetch_count'></a>
```cs
var builder = Host.CreateApplicationBuilder();
builder.UseWolverine(opts =>
{
    // One way or another, you're probably pulling the Azure Service Bus
    // connection string out of configuration
    var azureServiceBusConnectionString = builder
        .Configuration
        .GetConnectionString("azure-service-bus")!;

    opts.UseAzureServiceBus(azureServiceBusConnectionString).AutoProvision()

        // Optionally set a transport-wide default prefetch count that every
        // Azure Service Bus listener will inherit unless overridden
        .PrefetchCount(50);

    opts.ListenToAzureServiceBusQueue("incoming")

        // Have the Azure Service Bus client eagerly buffer up to 100 messages
        // on the client for just this queue, overriding the transport default.
        // Size this relative to MaximumMessagesToReceive and how fast your
        // handlers actually are -- prefetched messages age against the message
        // lock duration while they wait in the client buffer!
        .PrefetchCount(100)
        .MaximumMessagesToReceive(100)
        .BufferedInMemory();
});

using var host = builder.Build();
await host.StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Azure/Wolverine.AzureServiceBus.Tests/DocumentationSamples.cs#L316-L347' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_configuring_azure_service_bus_prefetch_count' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The configured prefetch count is applied to the `ServiceBusReceiver` used by buffered and durable
listeners, to the `ServiceBusSessionReceiver` used by session-based (`RequireSessions()`) listeners,
and as the initial `ServiceBusProcessorOptions.PrefetchCount` for inline (`ProcessInline()`)
listeners — where `ConfigureProcessor()` can still override it.

::: warning
Prefetched messages are locked for this consumer the moment the client buffers them, and their lock
duration keeps ticking while they sit in the client-side buffer waiting for your handlers. If the
prefetch buffer is oversized relative to how fast the endpoint actually processes messages, messages
at the back of the buffer expire their locks before they are ever handled, and Azure Service Bus
redelivers them — producing duplicate processing and, eventually, dead-lettered messages that were
never really failing.

Size `PrefetchCount` so that the *entire* prefetched batch can be processed comfortably within the
queue's lock duration:

- A good starting point is the Azure guidance of a small multiple (1-3x) of `MaximumMessagesToReceive`
  (Wolverine's default is 20), rather than some huge number.
- Estimate `(PrefetchCount + MaximumMessagesToReceive) * average handler latency` and keep that
  figure well under the queue's or subscription's lock duration (30 seconds by default).
- Fast handlers (a few milliseconds) can profitably use large prefetch buffers; slow handlers
  (hundreds of milliseconds or more) should use a small prefetch count or leave prefetch disabled.
:::

## Conventional Listener Configuration

In the case of listening to a large number of queues, it may be beneficial
to apply configuration to all the Azure Service Bus listeners like this:

<!-- snippet: sample_conventional_listener_configuration_for_azure_service_bus -->
<a id='snippet-sample_conventional_listener_configuration_for_azure_service_bus'></a>
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
    opts.UseAzureServiceBus(azureServiceBusConnectionString).AutoProvision()
        // Apply default configuration to all Azure Service Bus listeners
        // This can be overridden explicitly by any configuration for specific
        // listening endpoints
        .ConfigureListeners(listener => { listener.UseDurableInbox(new BufferingLimits(500, 100)); });
});

using var host = builder.Build();
await host.StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Azure/Wolverine.AzureServiceBus.Tests/DocumentationSamples.cs#L534-L555' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_conventional_listener_configuration_for_azure_service_bus' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Note that any of these settings would be overridden by specific configuration to
a specific endpoint.

