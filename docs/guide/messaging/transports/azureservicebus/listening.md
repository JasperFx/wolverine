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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Azure/Wolverine.AzureServiceBus.Tests/DocumentationSamples.cs#L498-L519' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_conventional_listener_configuration_for_azure_service_bus' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Note that any of these settings would be overridden by specific configuration to
a specific endpoint.

