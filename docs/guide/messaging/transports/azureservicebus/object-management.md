# Object Management

When using the Azure Service Bus transport, Wolverine is able to use the stateful resource model where all missing 
queues, topics, and subscriptions would be built at application start up time with this option applied:

<!-- snippet: sample_resource_setup_with_azure_service_bus -->
<a id='snippet-sample_resource_setup_with_azure_service_bus'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.UseAzureServiceBus("some connection string");

        // Make sure that all known resources like
        // the Azure Service Bus queues, topics, and subscriptions
        // configured for this application exist at application start up
        opts.Services.AddResourceSetupOnStartup();
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Azure/Wolverine.AzureServiceBus.Tests/Samples.cs#L54-L67' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_resource_setup_with_azure_service_bus' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

You can also direct Wolverine to build out Azure Service Bus object on demand as needed with:

<!-- snippet: sample_auto_provision_with_azure_service_bus -->
<a id='snippet-sample_auto_provision_with_azure_service_bus'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.UseAzureServiceBus("some connection string")

            // Wolverine will build missing queues, topics, and subscriptions
            // as necessary at runtime
            .AutoProvision();
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Azure/Wolverine.AzureServiceBus.Tests/Samples.cs#L72-L84' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_auto_provision_with_azure_service_bus' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

You can also opt to auto-purge all queues (there's also an option to do this queue by queue) on application
start up time with:

<!-- snippet: sample_auto_purge_with_azure_service_bus -->
<a id='snippet-sample_auto_purge_with_azure_service_bus'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.UseAzureServiceBus("some connection string")
            .AutoPurgeOnStartup();
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Azure/Wolverine.AzureServiceBus.Tests/Samples.cs#L89-L98' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_auto_purge_with_azure_service_bus' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

And lastly, because Azure Service Bus is a centralized broker model and you may have to share a single
environment between multiple developers or development environments, you can use prefixing with Azure Service Bus
so that every queue, topic, and subscription is quietly prefixed for uniqueness:



## Configuring Queues

If Wolverine is provisioning the queues for you, you can use one of these options
shown below to directly control exactly how the Azure Service Bus queue will be configured:

<!-- snippet: sample_configuring_azure_service_bus_queues -->
<a id='snippet-sample_configuring_azure_service_bus_queues'></a>
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

        // Alter how a queue should be provisioned by Wolverine
        .ConfigureQueue("outgoing", options => { options.AutoDeleteOnIdle = 5.Minutes(); });

    // Or do the same thing when creating a listener
    opts.ListenToAzureServiceBusQueue("incoming")
        .ConfigureQueue(options => { options.MaxDeliveryCount = 5; });

    // Or as part of a subscription
    opts.PublishAllMessages()
        .ToAzureServiceBusQueue("outgoing")
        .ConfigureQueue(options => { options.LockDuration = 3.Seconds(); })

        // You may need to change the maximum number of messages
        // in message batches depending on the size of your messages
        // if you hit maximum data constraints
        .MessageBatchSize(50);
});

using var host = builder.Build();
await host.StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Azure/Wolverine.AzureServiceBus.Tests/DocumentationSamples.cs#L49-L84' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_configuring_azure_service_bus_queues' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

