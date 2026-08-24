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
`MaxOutstandingMessages` is a bound on the **whole** listener. `ListenerCount` does **not** multiply it.

Google's own API remarks say that a `SubscriberClient` builds several inner clients and that "each will
observe the flow control settings independently", which reads as though the effective ceiling were
`ListenerCount × MaxOutstandingMessages`. Measured against the real client library, that is not what
happens — the client builds a single shared flow controller. Sizing a listener from the documented reading
will over-provision by a factor of `ListenerCount`. See
[#4067](https://github.com/JasperFx/wolverine/issues/4067).
:::

If the subscription was created with message ordering enabled, Pub/Sub additionally serializes delivery per
ordering key — Wolverine maps `Envelope.GroupId` onto that key. Effective concurrency then becomes the
*lesser* of `MaxOutstandingMessages` and the number of distinct group ids currently in flight, so an
endpoint with only a handful of hot group ids will run at that group count no matter how it is sized.
