# Topics and Subscriptions

Wolverine.AzureServiceBus supports [Azure Service Bus topics and subscriptions](https://learn.microsoft.com/en-us/azure/service-bus-messaging/service-bus-queues-topics-subscriptions).

To register endpoints to send messages to topics or to receive messages from subscriptions, use this syntax:

<!-- snippet: sample_using_azure_service_bus_subscriptions_and_topics -->
<a id='snippet-sample_using_azure_service_bus_subscriptions_and_topics'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.UseAzureServiceBus("some connection string")

            // If this is part of your configuration, Wolverine will try to create
            // any missing topics or subscriptions in the configuration at application
            // start up time
            .AutoProvision();

        // Publish to a topic
        opts.PublishMessage<Message1>().ToAzureServiceBusTopic("topic1")

            // Option to configure how the topic would be configured if
            // built by Wolverine
            .ConfigureTopic(topic =>
            {
                topic.MaxSizeInMegabytes = 100;
            });

        opts.ListenToAzureServiceBusSubscription("subscription1", subscription =>
            {
                // Optionally alter how the subscription is created or configured in Azure Service Bus
                subscription.DefaultMessageTimeToLive = 5.Minutes();
            })
            .FromTopic("topic1", topic =>
            {
                // Optionally alter how the topic is created in Azure Service Bus
                topic.DefaultMessageTimeToLive = 5.Minutes();
            });
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Azure/Wolverine.AzureServiceBus.Tests/Samples.cs#L25-L59' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_azure_service_bus_subscriptions_and_topics' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

To fully utilize subscription listening, be careful with using [Requeue error handling](/guide/handlers/error-handling) actions. In order to truly make
that work, Wolverine tries to build out a queue called `wolverine.response.[Your Wolverine service name]` specifically for
requeues from subscription listening. If your Wolverine application doesn't have permissions to create queues at runtime,
you may want to build that queue manually or forgo using "Requeue" as an error handling technique.

## Topics Owned by Another Application <Badge type="tip" text="6.62" />

When you listen to a subscription on a topic that another service owns, you want Wolverine to provision your
subscription and its rule, but never the topic: a topic created by the consumer gets default `CreateTopicOptions`, and
settings like partitioning and duplicate detection can't be changed afterwards. Mark the topic as externally owned:

```csharp
opts.ListenToAzureServiceBusSubscription("my-sub")
    .FromTopic("orders")
    .TopicIsExternallyOwned();

// or, when publishing to it
opts.PublishMessage<OrderPlaced>().ToAzureServiceBusTopic("orders").ExternallyOwned();
```

Wolverine will never create or delete an externally owned topic, regardless of `AutoProvision()`. Subscriptions on it
are still provisioned, and if the topic does not exist the error names the topic.

## Topic Filters

If Wolverine is provisioning the subscriptions for you, you can customize the subscription filter being created.

<!-- snippet: sample_configuring_azure_service_bus_subscription_filter -->
<a id='snippet-sample_configuring_azure_service_bus_subscription_filter'></a>
```cs
opts.ListenToAzureServiceBusSubscription(
        "subscription1",
        configureSubscriptionRule: rule =>
        {
            rule.Filter = new SqlRuleFilter("NOT EXISTS(user.ignore) OR user.ignore NOT LIKE 'true'");
        })
    .FromTopic("topic1");
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Azure/Wolverine.AzureServiceBus.Tests/DocumentationSamples.cs#L397-L406' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_configuring_azure_service_bus_subscription_filter' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The default filter if not customized is a simple `1=1` (always true) filter.

For more information regarding subscription filters, see the [Azure Service Bus documentation](https://learn.microsoft.com/en-us/azure/service-bus-messaging/topic-filters).
