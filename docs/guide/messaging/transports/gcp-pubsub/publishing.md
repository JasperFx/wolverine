# Publishing

Configuring Wolverine subscriptions through GCP Pub/Sub topics is done with the `ToPubsubTopic()` extension method shown in the example below:

<!-- snippet: sample_subscriber_rules_for_pubsub -->
<a id='snippet-sample_subscriber_rules_for_pubsub'></a>
```cs
var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.UsePubsub("your-project-id");

        opts
            .PublishMessage<Message1>()
            .ToPubsubTopic("outbound1");

        opts
            .PublishMessage<Message2>()
            .ToPubsubTopic("outbound2")
            .ConfigurePubsubTopic(options =>
            {
                options.MessageRetentionDuration =
                    Duration.FromTimeSpan(TimeSpan.FromMinutes(10));
            });
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/GCP/Wolverine.Pubsub.Tests/DocumentationSamples.cs#L105-L125' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_subscriber_rules_for_pubsub' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Ordering keys and `GroupId`

When Wolverine publishes to a Pub/Sub topic it stamps the outgoing message's `OrderingKey` from the
envelope's `GroupId`:

```cs
await bus.PublishAsync(new StatusUpdate(orderId), new DeliveryOptions
{
    // becomes the Pub/Sub message's OrderingKey
    GroupId = orderId.ToString()
});
```

A group id also arrives on the envelope automatically when the message is routed through
[message partitioning](/guide/messaging/partitioning), so endpoints using `MessagePartitioning` publish
ordering keys whether or not they ask for them explicitly.

::: warning
Ordering keys are not free. If the receiving **subscription** has `EnableMessageOrdering` turned on, Pub/Sub
will not dispatch a second message for an ordering key while one is still outstanding — so the consumer's
effective concurrency drops to the number of distinct group ids in flight, no matter how its flow control is
sized. This is the usual explanation for a Pub/Sub listener that runs far below the concurrency it was
configured for. See
[Ordering keys cap concurrency](/guide/messaging/transports/gcp-pubsub/listening#ordering-keys-cap-concurrency-at-the-number-of-distinct-group-ids)
for the details and how to confirm it.
:::
