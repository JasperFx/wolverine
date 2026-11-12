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
        .UseConventionalRouting(convention =>
        {
            // Optionally override the default queue naming scheme
            convention.QueueNameForSender(t => t.Namespace)

                // Optionally override the default queue naming scheme
                .QueueNameForListener(t => t.Namespace)

                // Fine tune the conventionally discovered listeners
                .ConfigureListeners((listener, builder) =>
                {
                    var messageType = builder.MessageType;
                    var runtime = builder.Runtime; // Access to basically everything

                    // customize the new queue
                    listener.CircuitBreaker(queue => { });

                    // other options...
                })

                // Fine tune the conventionally discovered sending endpoints
                .ConfigureSending((subscriber, builder) =>
                {
                    // Similarly, use the message type and/or wolverine runtime
                    // to customize the message sending
                });
        });
});

using var host = builder.Build();
await host.StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Azure/Wolverine.AzureServiceBus.Tests/DocumentationSamples.cs#L698-L742' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_conventional_routing_for_azure_service_bus' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Route to Topics and Subscriptions <Badge type="tip" text="1.6.0" />

You can also opt into conventional routing using topics and subscriptions named after the 
message type names like this:

<!-- snippet: sample_using_topic_and_subscription_conventional_routing_with_azure_service_bus -->
<a id='snippet-sample_using_topic_and_subscription_conventional_routing_with_azure_service_bus'></a>
```cs
opts.UseAzureServiceBusEmulator()
    .UseTopicAndSubscriptionConventionalRouting(convention =>
    {
        // Optionally control every aspect of the convention and
        // its applicability to types
        // as well as overriding any listener, sender, topic, or subscription
        // options

        // Can't use the full name because of limitations on name length
        convention.SubscriptionNameForListener(t => t.Name.ToLowerInvariant());
        convention.TopicNameForListener(t => t.Name.ToLowerInvariant());
        convention.TopicNameForSender(t => t.Name.ToLowerInvariant());
    })

    .AutoProvision()
    .AutoPurgeOnStartup();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Azure/Wolverine.AzureServiceBus.Tests/DocumentationSamples.cs#L264-L283' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_topic_and_subscription_conventional_routing_with_azure_service_bus' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Handler Type Naming <Badge type="tip" text="5.25" />

By default, conventional routing names queues after the **message type**. In modular monolith scenarios where you have
more than one handler for a given message type and want each handler to receive messages on its own dedicated queue,
you can opt into naming queues after the **handler type** instead:

```cs
opts.UseAzureServiceBus(connectionString)
    // Name listener queues after the handler type instead of the message type
    .UseConventionalRouting(NamingSource.FromHandlerType);
```

This also works with the topic/subscription variant:

```cs
opts.UseAzureServiceBus(connectionString)
    .UseTopicAndSubscriptionConventionalRouting(NamingSource.FromHandlerType);
```

With `NamingSource.FromHandlerType`, each handler class gets its own dedicated queue or subscription named after the
handler type. This ensures that each handler independently receives a copy of every message. Outgoing queue/topic
names are still derived from the message type.

## Several Handler Types for One Message Type <Badge type="tip" text="7.0" />

When a message type has more than one handler type, Wolverine gives each handler type its own handler chain
([see here](/guide/handlers/#multiple-handlers-for-the-same-message-type)), and this convention will create a subscription
for each separate handler using the handler type to derive the subscription name and the message type to derive
the topic name. Both the topic and subscription are declared by the transport if using the `AutoProvision()` setting.




## Several Handler Types with Queue Routing <Badge type="tip" text="6.49" />

The queue convention supports several handler types for one message type too. A queue does not fan out the
way a topic does, so the convention does the fan-out at the sender:

* each handler type listens on a queue named from the handler type, run through the transport's naming
  rules (the same name the topic convention gives its per-handler subscription);
* the sender publishes the message to each of those queues;
* a message type with a single, non-sticky handler type keeps the message type's own queue, as before.

```csharp
opts.UseAzureServiceBus("connection string")
    .AutoProvision()
    .UseConventionalRouting();
```

The per-handler queues are known only to the process that holds those handlers. A publisher in another process
publishes to the message type's queue alone, and nothing listens on that queue when every handler type has a queue of
its own, so none of the handlers receives the message. When several
processes need the same message, use the [topic and subscription convention](#route-to-topics-and-subscriptions)
instead; a topic is the Service Bus primitive for fan-out.
