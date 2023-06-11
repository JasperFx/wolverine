# Queue, Topic, and Binding Management

## Configuring Queues

If Wolverine is provisioning the queues for you, you can use one of these options
shown below to directly control exactly how the Azure Service Bus queue will be configured:

<!-- snippet: sample_configuring_azure_service_bus_queues -->
<a id='snippet-sample_configuring_azure_service_bus_queues'></a>
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

            // Alter how a queue should be provisioned by Wolverine
            .ConfigureQueue("outgoing", options => { options.AutoDeleteOnIdle = 5.Minutes(); });

        // Or do the same thing when creating a listener
        opts.ListenToAzureServiceBusQueue("incoming")
            .ConfigureQueue(options => { options.MaxDeliveryCount = 5; });

        // Or as part of a subscription
        opts.PublishAllMessages()
            .ToAzureServiceBusQueue("outgoing")
            .ConfigureQueue(options => { options.LockDuration = 3.Seconds(); });
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Wolverine.AzureServiceBus.Tests/DocumentationSamples.cs#L48-L75' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_configuring_azure_service_bus_queues' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

