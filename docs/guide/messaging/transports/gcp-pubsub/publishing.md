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

## Ordering keys

Every message Wolverine publishes to a Pub/Sub topic can carry an `OrderingKey`, and Wolverine resolves that
key from three sources with a strict precedence:

| Precedence | Source | Set by |
| --- | --- | --- |
| 1 | `Envelope.GroupId` | `DeliveryOptions.GroupId`, or [message partitioning](/guide/messaging/partitioning) |
| 2 | `OrderMessagesBy()` | Per-topic function on the subscriber configuration |
| 3 | The message itself | A custom `IPubsubEnvelopeMapper` that stamps `OrderingKey` directly |

A group id on the envelope always wins. The `OrderMessagesBy()` function only applies when there is no group
id, and a mapper-supplied key only survives when neither of the first two produced anything. If none of the
three yields a value the message goes out unkeyed, which is the default.

### From the group id

The common case is to let the envelope's `GroupId` become the ordering key:

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

### From a per-topic function

Sometimes the value you want to order on is not the group id — a tenant id, a header, or something derived
from the message itself. `OrderMessagesBy()` is the per-topic escape hatch for exactly that, and it does not
require replacing the endpoint's envelope mapper:

```cs
var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.UsePubsub("your-project-id");

        opts.PublishMessage<StatusUpdate>()
            .ToPubsubTopic("status")

            // Order by tenant instead of by group id. Return null to leave
            // a given message unkeyed.
            .OrderMessagesBy(e => e.TenantId);
    }).StartAsync();
```

The function is evaluated on every publish through that topic, and returning `null` leaves that particular
message without an ordering key.

Note that this is a **publishing** concern, so it lives on the subscriber (`ToPubsubTopic()`) configuration
only — there is deliberately no equivalent on `ListenToPubsubTopic()`. Whether a *consumer* enforces ordering
keys is decided by `EnableMessageOrdering` on the subscription instead, which is reached through
`ConfigurePubsubSubscription()`.

::: warning
Ordering keys are not free. If the receiving **subscription** has `EnableMessageOrdering` turned on, Pub/Sub
will not dispatch a second message for an ordering key while an earlier one is still outstanding. The
consumer's effective concurrency therefore drops to the number of *distinct* ordering keys in flight, no
matter how `MaxOutstandingMessages` is sized — a listener sized for 100 outstanding messages whose traffic is
concentrated on 3 keys will process 3 at a time. This holds however the key was set, including by
`OrderMessagesBy()`, and it is the usual explanation for a Pub/Sub listener running far below the concurrency
it was configured for. See [Listening](/guide/messaging/transports/gcp-pubsub/listening) for the details.
:::
