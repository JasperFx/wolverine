# Listening

Setting up a Wolverine listener for an SQS queue is shown below:

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

## Endpoint Modes

Amazon SQS listeners support all four Wolverine [endpoint modes](/guide/messaging/listeners):

| Mode | Configured with | When the message is deleted from SQS |
| --- | --- | --- |
| `BufferedInMemory` (default) | `.BufferedInMemory()` | on receipt, **before** the handler runs |
| `Durable` | `.UseDurableInbox()` | after the inbox insert |
| `Inline` | `.ProcessInline()` | after the handler succeeds |
| `NativeAck` | `.ProcessInParallelWithNativeAcks()` | after the handler succeeds |

### Native Ack Processing <Badge type="tip" text="6.30" />

SQS qualifies for [`NativeAck`](/guide/messaging/listeners#native-ack-endpoints) on both counts
the mode requires. Deliveries are settled individually — `DeleteMessage` against the receipt
handle carried on the envelope, so there is no cumulative position that could implicitly settle
a neighbouring message — and a receipt handle stays valid regardless of which thread holds it,
so the execution block is free to settle in handler-completion order rather than delivery order.

```csharp
opts.ListenToSqsQueue("webhooks")
    .ProcessInParallelWithNativeAcks()
    .PartitionProcessingByGroupId(PartitionSlots.Five)
    .MaximumParallelMessages(10);
```

Two SQS-specific things follow.

**The visibility timeout is renewed for you, unconditionally.** Unlike RabbitMQ, where an
unacknowledged delivery lives until the channel closes, an unsettled SQS message is on a clock.
Under this mode Wolverine holds it for lane queue time *plus* handler time, and lane queue time
is unbounded by design — so Wolverine keeps issuing `ChangeMessageVisibilityBatch` for every
delivery still sitting in a lane, without consulting `ExtendVisibilityWhileHandling()` (which
remains an `Inline`-only opt-in). Idle lanes cost nothing, and `MaximumVisibilityExtension` is
still the ceiling. See [Performance and Throughput](/guide/messaging/transports/sqs/performance).

**`MaxNumberOfMessages` is the prefetch equivalent, and it defaults *down* here.** Instead of
the usual 10, a native-ack endpoint receives twice the number of lanes that can be busy at once
— the partition slot count when group-partitioned, otherwise `MaximumParallelMessages` — clamped
to the SQS maximum of 10. Under every other mode the surplus messages in a batch are deleted
before their handlers run, so a full batch is free and saves API calls; here each one sits in a
lane holding an unsettled delivery to renew and to redeliver on a crash. Setting the property
explicitly always wins:

```csharp
opts.ListenToSqsQueue("webhooks", q => q.MaxNumberOfMessages = 10)
    .ProcessInParallelWithNativeAcks()
    .Sequential();
```

### FIFO queues do not support native acks

::: warning Refused at bootstrap
Calling `ProcessInParallelWithNativeAcks()` on a `.fifo` queue throws at startup. This is a
deliberate refusal rather than an oversight.
:::

The tempting reading is that the SQS `MessageGroupId` is a broker-side analogue of Wolverine's
`Envelope.GroupId`, so a FIFO queue and `PartitionProcessingByGroupId()` ought to be a natural
pairing. They are not, and the two halves fail differently:

* **Without partitioning, the ordering is silently lost.** One FIFO receive can return several
  messages of the same message group, in order. The native-ack execution block runs up to
  `MaximumParallelMessages` messages concurrently, so those same-group messages execute at the
  same time. The queue keeps its guarantee right up to the moment Wolverine takes delivery, and
  then discards it — and a FIFO queue costs more and throughputs less than a standard queue
  precisely to buy that guarantee.

* **With partitioning, the two schemes stack and the broker-side one is held hostage.** SQS will
  not deliver another message of a group while any message of that group is in flight. Under this
  mode "in flight" means lane queue time plus handler time, and the mode's entire premise is that
  lane depth is unbounded and free. It is not free here: unrelated groups that hash into the same
  Wolverine slot queue ahead of a group's head, and SQS blocks that whole group for the duration.
  Every other mode escapes this — `Buffered` and `Durable` delete the message before the handler
  runs, and `Inline` holds it for exactly one handler — which makes `NativeAck` the only Wolverine
  mode that converts its own lane depth into broker-side group stalls.

For ordered processing on a FIFO queue, use `ProcessInline()`. For ordered processing *with*
throughput, shard the topology across several FIFO queues by group id and listen to each one
inline — see [FIFO Queues](/guide/messaging/transports/sqs/fifo-queues) and
[partitioned sequential messaging](/guide/messaging/partitioning). Ordering then lives entirely
on the broker side, where FIFO can actually enforce it.
