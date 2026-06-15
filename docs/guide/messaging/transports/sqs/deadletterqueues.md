# Dead Letter Queues

By default, Wolverine will try to move dead letter messages in SQS to a single, global queue named "wolverine-dead-letter-queue."

## Customizing the Default Dead Letter Queue Name <Badge type="tip" text="5.39" />

The built-in default name `wolverine-dead-letter-queue` is fine for a single deployment, but multi-environment AWS accounts that share a region collide on it across environments. Override the default for the entire SQS transport in one call:

```csharp
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.UseAmazonSqsTransport()
            .DefaultDeadLetterQueueName("my-service-dlq")
            .AutoProvision();

        // No per-listener config needed — every listener picks up the
        // transport-wide default unless it overrides explicitly.
        opts.ListenToSqsQueue("orders");
        opts.ListenToSqsQueue("shipments");
    }).StartAsync();
```

Resolution order, per listener:

1. `ConfigureDeadLetterQueue("name")` on the listener wins.
2. `DisableDeadLetterQueueing()` on the listener wins (no DLQ for that one).
3. Otherwise the transport-wide `DefaultDeadLetterQueueName(...)` is used.
4. If none of the above is set, the historical default `wolverine-dead-letter-queue` applies.

The supplied name is sanitized via the same SQS-name normalization (`SanitizeSqsName`) used by per-listener config, so dots and other illegal SQS characters get the same treatment everywhere. `DisableAllNativeDeadLetterQueues()` (covered below) still overrides everything; the global kill-switch takes precedence over both the transport default and any per-listener config.

Wolverine system queues (response and control queues, when `EnableSystemQueues()` is on) explicitly opt out of dead-lettering and are unaffected by the transport default — they still resolve to "no DLQ" regardless of `DefaultDeadLetterQueueName`.

## Per-listener overrides

The transport default still composes cleanly with the per-listener API. Override on a single queue at a time (or by conventions too of course) like:

<!-- snippet: sample_configuring_dead_letter_queue_for_sqs -->
<a id='snippet-sample_configuring_dead_letter_queue_for_sqs'></a>
```cs
var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.UseAmazonSqsTransport();

        // No dead letter queueing
        opts.ListenToSqsQueue("incoming")
            .DisableDeadLetterQueueing();

        // Use a different dead letter queue
        opts.ListenToSqsQueue("important")
            .ConfigureDeadLetterQueue("important_errors", q =>
            {
                // optionally configure how the dead letter queue itself
                // is built by Wolverine
                q.MaxNumberOfMessages = 1000;
            });
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/AWS/Wolverine.AmazonSqs.Tests/Samples/Bootstrapping.cs#L217-L237' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_configuring_dead_letter_queue_for_sqs' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Disabling All Native Dead Letter Queueing

In one stroke, you can disable all usage of native SQS queues for dead letter queueing with this 
syntax:

<!-- snippet: sample_disabling_all_sqs_dead_letter_queueing -->
<a id='snippet-sample_disabling_all_sqs_dead_letter_queueing'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.UseAmazonSqsTransportLocally()
            // Disable all native SQS dead letter queueing
            .DisableAllNativeDeadLetterQueues()
            .AutoProvision();

        opts.ListenToSqsQueue("incoming");
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/AWS/Wolverine.AmazonSqs.Tests/Bugs/disabling_dead_letter_queue.cs#L17-L29' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_disabling_all_sqs_dead_letter_queueing' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

This would force Wolverine to use any persistent envelope storage for dead letter queueing.

## Recovering Native Dead Letters to Durable Storage <Badge type="tip" text="6.9" />

By default an Amazon SQS dead letter queue is just another SQS queue — its messages are only visible
through the AWS console or SDK, and tools that manage Wolverine's *durable* dead letters (for example
[CritterWatch](https://github.com/JasperFx/CritterWatch)) can't see or replay them.

`EnableDeadLetterQueueRecovery()` starts a background listener that drains the native SQS dead letter
queue(s) and copies each message into Wolverine's durable dead letter storage (the
`wolverine_dead_letters` table), where it becomes queryable and replayable through `IDeadLetters`:

```csharp
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        // Durable message storage is required — the recovered dead letters
        // are written to the wolverine_dead_letters table.
        opts.PersistMessagesWithPostgresql(connectionString);

        opts.UseAmazonSqsTransport()
            .AutoProvision()
            // Drain every dead letter queue used by a listener and copy the
            // messages into Wolverine's durable dead letter storage.
            .EnableDeadLetterQueueRecovery();

        opts.ListenToSqsQueue("orders");
    }).StartAsync();
```

With no arguments, every distinct dead letter queue used by a listening SQS queue is drained. Pass
explicit names when the dead letter queues you want to recover from aren't directly attached to a
Wolverine listener — for example, queues fed by an SQS [native redrive policy](https://docs.aws.amazon.com/AWSSimpleQueueService/latest/SQSDeveloperGuide/sqs-dead-letter-queues.html)
that you manage yourself:

```csharp
opts.UseAmazonSqsTransport()
    .EnableDeadLetterQueueRecovery("orders-dlq", "payments-dlq");
```

The names are sanitized with the same `SanitizeSqsName` normalization used everywhere else, so dots
become hyphens consistently.

Each recovered message is reconstructed back into a Wolverine `Envelope`. When Wolverine itself moved
the message to the dead letter queue, the original exception type and message are preserved (stamped
on the envelope when it failed). A message is only deleted from the SQS dead letter queue *after* it
has been safely written to durable storage, so a transient database outage never loses a dead letter.

::: tip
This is the SQS equivalent of the [RabbitMQ dead letter recovery](../rabbitmq/deadletterqueues.html)
feature, and uses the same `EnableDeadLetterQueueRecovery()` syntax across every native-dead-letter
transport.
:::



