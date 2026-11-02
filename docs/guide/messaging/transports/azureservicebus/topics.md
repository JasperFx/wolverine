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

## Externally Owned Topics, Queues and Subscriptions <Badge type="tip" text="6.61" />

A service that listens to a topic usually doesn't own it. The topic belongs to the service that publishes to it,
and that service decides how it is created. If your service deploys first with `AutoProvision()` on, Wolverine
would create the topic with its own default `CreateTopicOptions`, and some of those, partitioning and duplicate
detection among them, can never be changed once the topic exists. Nor can permissions stop it: creating a
subscription needs the `Manage` claim, and `Manage` can create topics too.

Use `TopicIsExternallyOwned()` to have Wolverine provision your subscription and its rule, but never the topic:

<!-- snippet: sample_asb_topic_is_externally_owned -->
<a id='snippet-sample_asb_topic_is_externally_owned'></a>
```cs
opts.ListenToAzureServiceBusSubscription("shipping")
    .FromTopic("orders")

    // The "orders" topic belongs to the service that publishes to it.
    // Wolverine creates the "shipping" subscription and its rule on that
    // topic, but never creates, changes or deletes the topic itself
    .TopicIsExternallyOwned();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Azure/Wolverine.AzureServiceBus.Tests/externally_owned_topics_5342.cs#L52-L62' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_asb_topic_is_externally_owned' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

With the topic externally owned, Wolverine:

* never creates or deletes the topic, regardless of `AutoProvision()`
* still creates the subscription and its rule, and keeps the rule in step with your configuration, as it does today
* fails provisioning the subscription with an `ExternallyOwnedTopicNotFoundException` that names the topic if the
  topic doesn't exist yet, rather than with a bare `MessagingEntityNotFound`. At startup that failure is retried
  like any other broker failure, until `BrokerInitializationTimeout` runs out, so a topic created by its owner in
  the meantime is picked up

Queues and topics you only send to or read from can be marked `ExternallyOwned()` the same way:

<!-- snippet: sample_asb_queues_and_topics_are_externally_owned -->
<a id='snippet-sample_asb_queues_and_topics_are_externally_owned'></a>
```cs
// A topic another service owns and this one only publishes to
opts.PublishMessage<AuditEntryRecorded>()
    .ToAzureServiceBusTopic("audit")
    .ExternallyOwned();

// A queue another service owns and this one only reads from
opts.ListenToAzureServiceBusQueue("inbox")
    .ExternallyOwned();

// A queue another service owns and this one only sends to
opts.PublishMessage<InvoiceRequested>()
    .ToAzureServiceBusQueue("billing")
    .ExternallyOwned();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/Azure/Wolverine.AzureServiceBus.Tests/externally_owned_topics_5342.cs#L123-L139' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_asb_queues_and_topics_are_externally_owned' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

`ExternallyOwned()` on a subscription listener marks the *subscription itself* as owned elsewhere, and Wolverine
then only listens to it, without creating, changing or deleting it or its rules. That is rarely what you want for a
subscription your service reads from, which is why the topic has its own `TopicIsExternallyOwned()`.

`resources check` still checks that externally owned objects exist. See
[Externally Owned Broker Objects](/guide/messaging/transports/externally-owned) for how this works across every
transport.

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
