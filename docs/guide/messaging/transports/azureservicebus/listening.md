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
        .GetConnectionString("azure-service-bus");

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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Azure/Wolverine.AzureServiceBus.Tests/DocumentationSamples.cs#L89-L122' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_configuring_an_azure_service_bus_listener' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

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
        .GetConnectionString("azure-service-bus");

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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Azure/Wolverine.AzureServiceBus.Tests/DocumentationSamples.cs#L285-L307' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_conventional_listener_configuration_for_azure_service_bus' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Note that any of these settings would be overridden by specific configuration to
a specific endpoint.

