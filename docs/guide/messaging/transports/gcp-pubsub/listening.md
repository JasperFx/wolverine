# Listening

Setting up Wolverine listeners and GCP Pub/Sub subscriptions for GCP Pub/Sub topics is shown below:

<!-- snippet: sample_listen_to_pubsub_topic -->
<a id='snippet-sample_listen_to_pubsub_topic'></a>
```cs
var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.UsePubsub("your-project-id");

        opts.ListenToPubsubTopic("incoming1");

        // Listen to an existing subscription
        opts.ListenToPubsubSubscription("subscription1", x =>
        {
            // Other configuration...
        });

        opts.ListenToPubsubTopic("incoming2")

            // You can optimize the throughput by running multiple listeners
            // in parallel
            .ListenerCount(5)
            .ConfigurePubsubSubscription(options =>
            {
                // Optionally configure the subscription itself
                options.DeadLetterPolicy = new DeadLetterPolicy
                {
                    DeadLetterTopic = "errors",
                    MaxDeliveryAttempts = 5
                };
                options.AckDeadlineSeconds = 60;
                options.RetryPolicy = new RetryPolicy
                {
                    MinimumBackoff = Duration.FromTimeSpan(TimeSpan.FromSeconds(1)),
                    MaximumBackoff = Duration.FromTimeSpan(TimeSpan.FromSeconds(10))
                };
            });
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/GCP/Wolverine.Pubsub.Tests/DocumentationSamples.cs#L64-L100' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_listen_to_pubsub_topic' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Long running handlers and the ack extension budget

While Wolverine is processing a Pub/Sub message, the Pub/Sub client keeps the message's ack deadline alive
for it by repeatedly extending that deadline in the background. It will only do so for a bounded total
amount of time, controlled by `MaxTotalAckExtension`.

Wolverine sets that budget explicitly to **one hour**:

```cs
opts.ListenToPubsubTopic("incoming")
    .ConfigureListener(client =>
    {
        // The default. Raise it if you knowingly run handlers longer than an hour;
        // lower it if you would rather a wedged message be redelivered sooner.
        client.MaxTotalAckExtension = TimeSpan.FromHours(1);
    });
```

::: danger
When the budget is exhausted the Pub/Sub client simply **stops extending** the deadline. It does *not*
cancel your handler and it does *not* raise anything into it. The service then redelivers the message, so a
**second execution of that message begins while the first one is still running**.

This is more dangerous than ordinary at-least-once redelivery. Wolverine's delivery contract already means
your handlers must tolerate seeing a message twice — but here the two executions **overlap**. Anything that
assumes a message is not running concurrently with itself will be violated rather than merely retried:
optimistic concurrency checks, event stream appends, sagas, and the intra-group ordering guarantee of
`PartitionProcessingByGroupId`.
:::

Because the two failure modes are so lopsided — too low silently corrupts data, too high only delays the
redelivery of a genuinely stuck message and holds a flow control slot meanwhile — the default deliberately
errs generous. One hour is sixty times Wolverine's default `DefaultExecutionTimeout`, leaving ample room
for a slow handler and its inline retries.

Wolverine cannot prevent the overlap, but it will no longer let it happen silently. When a message outlives
the budget, the listener logs a warning naming the message id and how long it has been running:

```
pubsub://your-project-id/incoming: Google Cloud Platform Pub/Sub message 1480 has been processing for
00:01:02.9102363, which exceeds the MaxTotalAckExtension budget of 00:01:00. ...
```

Treat that warning as a correctness alarm, not a performance note. Either shorten the handler or raise
`MaxTotalAckExtension` to cover it.

::: tip
A handler that has *already* outlived the budget is not cancelled — it keeps running alongside its own
duplicate. Cancelling it instead is under consideration; see
[#4066](https://github.com/JasperFx/wolverine/issues/4066).
:::

## Concurrency and flow control

`MaxOutstandingMessages` bounds how many messages a listener may have in flight at once, and
`MaxOutstandingByteCount` does the same by payload size:

```cs
opts.ListenToPubsubTopic("incoming")
    .ConfigureListener(client =>
    {
        client.MaxOutstandingMessages = 1000;              // default
        client.MaxOutstandingByteCount = 100 * 1024 * 1024; // default
    });
```

::: warning
`MaxOutstandingMessages` is the bound for **one** `SubscriberClient`, and the several inner clients that
client builds for itself do **not** each get their own allowance.

Google's own API remarks say that a `SubscriberClient` creates multiple `SubscriberServiceApiClient`
instances and that "each will observe the flow control settings independently", which reads as though the
effective ceiling were `ClientCount × MaxOutstandingMessages`. Measured against the real client library
(3.24.0), that is not what happens — the SDK builds a single `Flow` and shares it across every inner
channel. Observed peak in flight tracked the configured limit exactly at 1 → 1, 3 → 3, 8 → 8 and
1000 → 1000, and with `ClientCount = 4` against a limit of 3 the peak was **3, not 12**. Wolverine never
sets `ClientCount` itself, so for a single listener `MaxOutstandingMessages` is simply the ceiling.
See [#4067](https://github.com/JasperFx/wolverine/issues/4067).
:::

`ListenerCount` is a different matter. Wolverine builds a separate listener — and therefore a separate
`SubscriberClient` with its own flow controller — for each one, so it *does* multiply. An endpoint declared
like this can hold up to 500 messages in flight, not 100:

```cs
opts.ListenToPubsubTopic("incoming")
    .ListenerCount(5)
    .ConfigureListener(client => client.MaxOutstandingMessages = 100);
```

Size the endpoint on `ListenerCount × MaxOutstandingMessages`, and treat `MaxOutstandingMessages` on its own
as the per-listener figure.

## Ordering keys cap concurrency at the number of distinct group ids

If you are diagnosing a Pub/Sub listener that runs slower than its flow control settings imply, and the
numbers above do not explain it, check whether **message ordering** is in play. It is the most common reason
a correctly-sized listener refuses to use the capacity you gave it, and nothing in the listener's own
configuration hints at it.

Pub/Sub guarantees ordered delivery per ordering key, and it implements that guarantee by **refusing to
dispatch a second message for a key while one is still outstanding**. Wolverine maps `Envelope.GroupId` onto
the ordering key when it publishes (see
[Publishing](/guide/messaging/transports/gcp-pubsub/publishing)), so any message carrying a group id
carries an ordering key too.

The consequence is that effective concurrency becomes the *lesser* of `MaxOutstandingMessages` and **the
number of distinct group ids currently in flight** — the configured bound stops being the binding constraint
as soon as the group count falls below it. A listener sized for 100 outstanding messages whose traffic is
concentrated on 3 group ids will process 3 messages at a time. Measured against a subscription with
`EnableMessageOrdering = true`, publishing 12 messages across 3 ordering keys and holding every callback
open, exactly 3 callbacks ran; the other 9 messages were never dispatched until their key freed up.

This only applies when the **subscription** has message ordering enabled, which is off by default:

```cs
opts.ListenToPubsubTopic("incoming")
    .ConfigurePubsubSubscription(options =>
    {
        // false by default -- without this, ordering keys on incoming
        // messages are carried but not enforced
        options.EnableMessageOrdering = true;
    });
```

That flag is applied when Wolverine *creates* the subscription. If you are listening to a subscription that
already exists, whether ordering is enforced was decided when that subscription was created, and you will
need to inspect it in the Google Cloud console rather than infer it from your Wolverine configuration.

::: tip
To confirm this is what you are hitting, compare the number of messages your listener holds concurrently
against the number of *distinct* group ids among them. If the two track each other while
`MaxOutstandingMessages` sits far above both, broker-side ordering is your ceiling. Widening the group id —
so that traffic spreads over more keys — raises throughput; raising `MaxOutstandingMessages` will not.
:::

Note that this serialisation overlaps with what
[`PartitionProcessingByGroupId`](/guide/messaging/partitioning) provides in process: both keep messages
sharing a group id from running concurrently. When ordering is enabled at the subscription, the broker's
serialisation is the binding one, because the second message never arrives to be partitioned. How the two
mechanisms should combine for a future native-ack Pub/Sub endpoint is still open — see
[#4052](https://github.com/JasperFx/wolverine/issues/4052).
