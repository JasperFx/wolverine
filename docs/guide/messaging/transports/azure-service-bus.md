# Using Azure Service Bus

::: tip
Wolverine is only supporting Azure Service Bus queues for right now, but support for publishing
or subscribing through [Azure Service Bus topics and subscriptions](https://learn.microsoft.com/en-us/azure/service-bus-messaging/service-bus-queues-topics-subscriptions) is planned.
:::

Wolverine supports [Azure Service Bus](https://learn.microsoft.com/en-us/azure/service-bus-messaging/service-bus-messaging-overview) as a messaging transport through the WolverineFx.AzureServiceBus nuget.

## Connecting to the Broker

After referencing the Nuget package, the next step to using Azure Service Bus within your Wolverine
application is to connect to the service broker using the `UseAzureServiceBus()` extension
method as shown below in this basic usage:

<!-- snippet: sample_basic_connection_to_azure_service_bus -->
<a id='snippet-sample_basic_connection_to_azure_service_bus'></a>
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
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Wolverine.AzureServiceBus.Tests/DocumentationSamples.cs#L15-L42' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_basic_connection_to_azure_service_bus' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The advanced configuration for the broker is the [ServiceBusClientOptions](https://learn.microsoft.com/en-us/dotnet/api/azure.messaging.servicebus.servicebusclientoptions?view=azure-dotnet) class from the Azure.Messaging.ServiceBus
library. 

For security purposes, there are overloads of `UseAzureServiceBus()` that will also accept and opt into Azure Service Bus authentication with:

1. [TokenCredential](https://learn.microsoft.com/en-us/dotnet/api/azure.core.tokencredential?view=azure-dotnet)
2. [AzureNamedKeyCredential](https://learn.microsoft.com/en-us/dotnet/api/azure.azurenamedkeycredential?view=azure-dotnet)
3. [AzureSasCredential](https://learn.microsoft.com/en-us/dotnet/api/azure.azuresascredential?view=azure-dotnet)

## Listening to Queues

::: warning
The Azure Service Bus transport uses batching to both send and receive messages. As such,
the listeners or senders can only be configured to use buffered or durable mechanics. I.e., there
is no current option for inline senders or listeners.
:::

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

## Listening to Queues

You can configure explicit queue listening with this syntax:

<!-- snippet: sample_configuring_an_azure_service_bus_listener -->
<a id='snippet-sample_configuring_an_azure_service_bus_listener'></a>
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
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Wolverine.AzureServiceBus.Tests/DocumentationSamples.cs#L81-L111' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_configuring_an_azure_service_bus_listener' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Publishing to Queues

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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Wolverine.AzureServiceBus.Tests/DocumentationSamples.cs#L116-L137' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_publishing_to_specific_azure_service_bus_queue' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Conventional Listener Configuration

In the case of listening to a large number of queues, it may be beneficial
to apply configuration to all the Azure Service Bus listeners like this:

<!-- snippet: sample_conventional_listener_configuration_for_azure_service_bus -->
<a id='snippet-sample_conventional_listener_configuration_for_azure_service_bus'></a>
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
            // Apply default configuration to all Azure Service Bus listeners
            // This can be overridden explicitly by any configuration for specific
            // listening endpoints
            .ConfigureListeners(listener => { listener.UseDurableInbox(new BufferingLimits(500, 100)); });
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Wolverine.AzureServiceBus.Tests/DocumentationSamples.cs#L172-L191' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_conventional_listener_configuration_for_azure_service_bus' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Note that any of these settings would be overridden by specific configuration to
a specific endpoint.

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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Wolverine.AzureServiceBus.Tests/DocumentationSamples.cs#L196-L215' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_conventional_subscriber_configuration_for_azure_service_bus' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Note that any of these settings would be overridden by specific configuration to
a specific endpoint.

## Conventional Message Routing

Lastly, you can have Wolverine automatically determine message routing to Azure Service Bus
based on conventions as shown in the code snippet below. By default, this approach assumes that
each outgoing message type should be sent to queue named with the [message type name](/guide/messages.html#message-type-name-or-alias) for that 
message type. 

Likewise, Wolverine sets up a listener for a queue named similarly for each message type known
to be handled by the application.

<!-- snippet: sample_conventional_routing_for_azure_service_bus -->
<a id='snippet-sample_conventional_routing_for_azure_service_bus'></a>
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
            .UseConventionalRouting(convention =>
            {
                // Optionally override the default queue naming scheme
                convention.QueueNameForSender(t => t.Namespace)

                    // Optionally override the default queue naming scheme
                    .QueueNameForListener(t => t.Namespace)

                    // Fine tune the conventionally discovered listeners
                    .ConfigureListeners((listener, context) =>
                    {
                        var messageType = context.MessageType;
                        var runtime = context.Runtime; // Access to basically everything

                        // customize the new queue
                        listener.CircuitBreaker(queue => { });

                        // other options...
                    })

                    // Fine tune the conventionally discovered sending endpoints
                    .ConfigureSending((subscriber, context) =>
                    {
                        // Similarly, use the message type and/or wolverine runtime
                        // to customize the message sending
                    });
            });
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Wolverine.AzureServiceBus.Tests/DocumentationSamples.cs#L221-L263' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_conventional_routing_for_azure_service_bus' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->
