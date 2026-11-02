# Configuring Queues

Both listening and publishing endpoints in the Amazon SQS transport are backed by an `AmazonSqsQueue` endpoint.
This page covers how Wolverine *creates* those queues and how to customize the queue attributes (visibility
timeout, message retention, maximum message size, and so on). For receiving messages see
[Listening](/guide/messaging/transports/sqs/listening) and for sending see
[Publishing](/guide/messaging/transports/sqs/publishing).

## Auto-Provisioning Queues

By default Wolverine will *not* create any SQS queues for you. To let Wolverine create any missing queues at
application startup, opt into auto-provisioning on the transport:

```cs
opts.UseAmazonSqsTransport()

    // Let Wolverine create missing queues as necessary
    .AutoProvision();
```

When `AutoProvision()` is enabled, every queue endpoint is created (if it does not already exist) using its
`CreateQueueRequest` configuration. Without `AutoProvision()`, Wolverine assumes the queues already exist and
simply resolves their queue URLs at runtime.

::: tip
Creating SQS queues requires IAM permissions that your application may not have in production. It's common to
enable `AutoProvision()` only in development/test environments and to pre-create queues through your
infrastructure-as-code tooling in production.
:::

You can also optionally purge all queues on startup with `AutoPurgeOnStartup()`, though be aware this can be
slow:

```cs
opts.UseAmazonSqsTransport()
    .AutoProvision()

    // Optionally purge all queues on application startup.
    // Beware that this is potentially slow
    .AutoPurgeOnStartup();
```

## Externally Owned Queues <Badge type="tip" text="6.62" />

`AutoProvision()` is all or nothing for the queues Wolverine knows about. When a queue belongs to another service,
you don't want your service creating it first with Wolverine's default `CreateQueueRequest`, re-creating it after
its owner deletes it, or deleting it in `resources teardown`. Mark that queue with `ExternallyOwned()` and Wolverine
leaves its lifecycle to its owner, while still provisioning the queues you do own:

<!-- snippet: sample_sqs_queue_is_externally_owned -->
<a id='snippet-sample_sqs_queue_is_externally_owned'></a>
```cs
// The queue belongs to another service. Wolverine reads from it, but
// never creates, re-creates or deletes it, even with AutoProvision() on
opts.ListenToSqsQueue("gh5342-owners-inbox")
    .ExternallyOwned();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/AWS/Wolverine.AmazonSqs.Tests/externally_owned_queues_5342.cs#L53-L60' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_sqs_queue_is_externally_owned' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The same works for a queue you only send to:

<!-- snippet: sample_sqs_publish_to_externally_owned_queue -->
<a id='snippet-sample_sqs_publish_to_externally_owned_queue'></a>
```cs
// A queue another service owns and this one only sends to
opts.PublishMessage<InvoiceRequested>()
    .ToSqsQueue("gh5342-owners-billing")
    .ExternallyOwned();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/AWS/Wolverine.AmazonSqs.Tests/externally_owned_queues_5342.cs#L94-L101' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_sqs_publish_to_externally_owned_queue' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

For an externally owned queue, Wolverine:

* never creates the queue at startup or in `resources setup`, regardless of `AutoProvision()`
* never re-creates it when a listener finds it missing, as it otherwise would for a queue it provisioned
* never deletes it in `resources teardown`
* never creates it on a tenant's account when you use a broker per tenant
* fails at startup with an error that names the queue and says it is externally owned if the queue doesn't exist,
  rather than with the SDK's bare "The specified queue does not exist"

`resources check` still checks that externally owned queues exist. A dead letter queue is its own queue, so a
listener on an externally owned queue still provisions Wolverine's dead letter queue unless you call
`DisableDeadLetterQueueing()` or point it at a queue that is marked `ExternallyOwned()` as well. See
[Externally Owned Broker Objects](/guide/messaging/transports/externally-owned) for how this works across every
transport.

## Configuring Queue Attributes

Each `AmazonSqsQueue` endpoint exposes a `Configuration` property that is the actual
[`CreateQueueRequest`](https://docs.aws.amazon.com/sdkfornet/v3/apidocs/items/SQS/TCreateQueueRequest.html) used
when Wolverine provisions the queue. You can set any SQS queue attribute directly on `Configuration.Attributes`.

When configuring a listener, the second argument gives you direct access to the queue:

<!-- snippet: sample_listen_to_sqs_queue -->
<a id='snippet-sample_listen_to_sqs_queue'></a>
```cs
var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.UseAmazonSqsTransport()

            // Let Wolverine create missing queues as necessary
            .AutoProvision()

            // Optionally purge all queues on application startup.
            // Warning though, this is potentially slow
            .AutoPurgeOnStartup();

        opts.ListenToSqsQueue("incoming", queue =>
            {
                queue.Configuration.Attributes[QueueAttributeName.DelaySeconds]
                    = "5";

                queue.Configuration.Attributes[QueueAttributeName.MessageRetentionPeriod]
                    = 4.Days().TotalSeconds.ToString();
            })
            // You can optimize the throughput by running multiple listeners
            // in parallel
            .ListenerCount(5);
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/AWS/Wolverine.AmazonSqs.Tests/Samples/Bootstrapping.cs#L147-L173' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_listen_to_sqs_queue' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

When configuring a subscription, use `ConfigureQueueCreation()` to reach the same `CreateQueueRequest`:

<!-- snippet: sample_subscriber_rules_for_sqs -->
<a id='snippet-sample_subscriber_rules_for_sqs'></a>
```cs
var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.UseAmazonSqsTransport();

        opts.PublishMessage<Message1>()
            .ToSqsQueue("outbound1")

            // Increase the outgoing message throughput, but at the cost
            // of strict ordering
            .MessageBatchMaxDegreeOfParallelism(Environment.ProcessorCount);

        opts.PublishMessage<Message2>()
            .ToSqsQueue("outbound2").ConfigureQueueCreation(request =>
            {
                request.Attributes[QueueAttributeName.MaximumMessageSize] = "1024";
            });
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/AWS/Wolverine.AmazonSqs.Tests/Samples/Bootstrapping.cs#L178-L199' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_subscriber_rules_for_sqs' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

`ConfigureQueueCreation(Action<CreateQueueRequest>)` is available on both the listener and the subscriber
configuration, so you can customize the create request regardless of which side declares the queue.

### Strongly-Typed Attribute Helpers

For the most common attributes, Wolverine exposes extension methods on `AmazonSqsQueue` so you don't have to
remember the attribute names or stringify values yourself:

| Helper | SQS attribute |
|---|---|
| `MaximumMessageSize(int bytes)` | `MaximumMessageSize` |
| `MessageRetentionPeriod(int seconds)` | `MessageRetentionPeriod` |
| `ReceiveMessageWaitTimeSeconds(int seconds)` | `ReceiveMessageWaitTimeSeconds` |
| `VisibilityTimeout(int seconds)` | `VisibilityTimeout` |

```cs
opts.ListenToSqsQueue("incoming", queue =>
{
    queue
        // Maximum message size in bytes (default 256 KB)
        .MaximumMessageSize(256 * 1024)

        // How long SQS retains an unconsumed message, in seconds
        .MessageRetentionPeriod((int)4.Days().TotalSeconds)

        // The visibility timeout (in seconds) for messages pulled
        // off of this queue
        .VisibilityTimeout(120);
});
```

Any attribute that does not have a dedicated helper can still be set through `Configuration.Attributes` using the
`QueueAttributeName` constants from the AWS SDK, exactly as shown in the listener snippet above.

## Relationship to Listening and Publishing

The queue configuration described here is shared by both directions of communication:

- [Listening](/guide/messaging/transports/sqs/listening) — receiving messages from a queue.
- [Publishing](/guide/messaging/transports/sqs/publishing) — routing outgoing messages to a queue.
- [FIFO Queues](/guide/messaging/transports/sqs/fifo-queues) — creating ordered, exactly-once queues by adding
  the `FifoQueue` and `ContentBasedDeduplication` attributes.

It does not matter whether a queue is first declared by a listener or by a subscription — Wolverine tracks a
single `AmazonSqsQueue` per queue name, so its `CreateQueueRequest` configuration is applied once when the queue
is provisioned.
