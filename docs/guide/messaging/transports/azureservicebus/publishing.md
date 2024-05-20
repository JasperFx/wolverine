# Publishing

You can configure explicit subscription rules to Azure Service Bus queues
with this usage:

<!-- snippet: sample_publishing_to_specific_azure_service_bus_queue -->
<a id='snippet-sample_publishing_to_specific_azure_service_bus_queue'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine((context, opts) =>
    {
        // One way or another, you're probably pulling the Azure Service Bus
        // connection string out of configuration
        var azureServiceBusConnectionString = context
            .Configuration
            .GetConnectionString("azure-service-bus");

        // Connect to the broker in the simplest possible way
        opts.UseAzureServiceBus(azureServiceBusConnectionString).AutoProvision();

        // Explicitly configure sending messages to a specific queue
        opts.PublishAllMessages().ToAzureServiceBusQueue("outgoing")

            // All the normal Wolverine options you'd expect
            .BufferedInMemory();
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Azure/Wolverine.AzureServiceBus.Tests/DocumentationSamples.cs#L205-L226' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_publishing_to_specific_azure_service_bus_queue' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->


## Conventional Subscriber Configuration

In the case of publishing to a large number of queues, it may be beneficial
to apply configuration to all the Azure Service Bus subscribers like this:

<!-- snippet: sample_conventional_subscriber_configuration_for_azure_service_bus -->
<a id='snippet-sample_conventional_subscriber_configuration_for_azure_service_bus'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine((context, opts) =>
    {
        // One way or another, you're probably pulling the Azure Service Bus
        // connection string out of configuration
        var azureServiceBusConnectionString = context
            .Configuration
            .GetConnectionString("azure-service-bus");

        // Connect to the broker in the simplest possible way
        opts.UseAzureServiceBus(azureServiceBusConnectionString).AutoProvision()
            // Apply default configuration to all Azure Service Bus subscribers
            // This can be overridden explicitly by any configuration for specific
            // sending/subscribing endpoints
            .ConfigureSenders(sender => sender.UseDurableOutbox());
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Azure/Wolverine.AzureServiceBus.Tests/DocumentationSamples.cs#L285-L304' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_conventional_subscriber_configuration_for_azure_service_bus' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Note that any of these settings would be overridden by specific configuration to
a specific endpoint.
