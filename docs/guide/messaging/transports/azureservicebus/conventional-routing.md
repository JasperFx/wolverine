# Conventional Message Routing

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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Azure/Wolverine.AzureServiceBus.Tests/DocumentationSamples.cs#L281-L323' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_conventional_routing_for_azure_service_bus' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Route to Topics and Subscriptions

::: info
This option was introduced in Wolverine 1.6.0.
:::

You can also opt into conventional routing using topics and subscriptions named after the 
message type names like this:

<!-- snippet: sample_using_topic_and_subscription_conventional_routing_with_azure_service_bus -->
<a id='snippet-sample_using_topic_and_subscription_conventional_routing_with_azure_service_bus'></a>
```cs
opts.UseAzureServiceBusTesting()
    .UseTopicAndSubscriptionConventionalRouting(convention =>
    {
        // Optionally control every aspect of the convention and
        // its applicability to types
        // as well as overriding any listener, sender, topic, or subscription
        // options
    })

    .AutoProvision()
    .AutoPurgeOnStartup();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Azure/Wolverine.AzureServiceBus.Tests/ConventionalRouting/Broadcasting/end_to_end_with_conventional_routing.cs#L26-L40' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_topic_and_subscription_conventional_routing_with_azure_service_bus' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->


