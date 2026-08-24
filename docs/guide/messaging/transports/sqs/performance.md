# Performance Tuning

This page collects the levers that matter most for throughput and latency with the Amazon SQS
transport, and the factors behind them.

## The receive side: batches in, singles out

Wolverine receives from SQS in batches — each poll asks for up to `MaxNumberOfMessages`
(default **10**, the SQS maximum) with long polling enabled by default (`WaitTimeSeconds` = 5).
Durable endpoints benefit doubly: the whole received batch is written to the database inbox in
a **single** batched insert, so durable SQS endpoints are considerably cheaper per message than
push-based transports.

Message *completion* is batched too. Completed messages accumulate for up to 50 milliseconds and
are deleted with a single `DeleteMessageBatch` call of up to 10 — so a full 10-message receive is
settled with one round trip instead of ten. Since SQS bills per API call, this is a cost lever as
much as a latency one. The practical levers:

```cs
opts.ListenToSqsQueue("orders")
    // Parallel pollers: N independent receive loops on the same queue.
    // The main receive-side throughput lever today.
    .ListenerCount(4)

    // Long-poll duration. Raise toward 20s for low-traffic queues to cut
    // empty-receive API calls (and cost); keep short only if you need
    // faster listener shutdown.
    .ConfigureListener(l => l.WaitTimeSeconds = 20)

    // Delete batching. Defaults to 10 (the SQS maximum) with a 50ms window.
    // Pass 1 to go back to one DeleteMessage call per message.
    .DeleteMessageBatchSize(10, 50.Milliseconds())

    .MaximumParallelMessages(10);
```

::: tip
The delete window is a *maximum batch age*, not a quiet period, so a single message never waits
longer than the window. Batches are also flushed when a listener stops, so a paused listener
does not leave settled messages to reappear at their visibility timeout.
:::

## Visibility timeout: size it against your processing window

Wolverine sets each received message's visibility timeout at receive (default **120 seconds**)
and, unless you opt into the inline heartbeat described below, never calls
`ChangeMessageVisibility` to extend it. How much of your processing that window has to cover
depends on the endpoint mode, because each mode deletes the message from SQS at a different
point:

- **Durable** endpoints delete the message as soon as it has been written to the durable
  inbox — *before* the handler runs. The local backlog lives in your database, not under an
  SQS timer, so the visibility timeout only has to cover the receive-to-insert round trip.
  If that insert is ever slower than the timeout (an overloaded database), SQS redelivers and
  the inbox deduplicates the copy — wasted work, not duplicate side effects.
- **Buffered** endpoints delete messages *as soon as they are buffered*, before handling — so
  the visibility timeout is moot, and instead of duplicates you get at-most-once semantics: an
  ungraceful crash loses the buffered backlog.
- **Inline** endpoints delete only after successful handling, so the visibility timeout must
  cover the **whole received batch**, not just one handler: an inline listener works through
  the up-to-10 messages of a receive one at a time, so the last message of the batch has been
  aging against the timeout through every handler before it. Past the timeout SQS redelivers
  the message *while it is still being handled* (or still waiting its turn), the second copy
  executes too, and the first copy's eventual delete carries a stale receipt handle that SQS
  accepts without deleting anything. Either raise the timeout on the endpoint
  (`.VisibilityTimeout(...)`) to comfortably exceed `MaxNumberOfMessages × your slowest
  handler`, or turn on the heartbeat:

<!-- snippet: sample_sqs_extend_visibility_while_handling -->
<a id='snippet-sample_sqs_extend_visibility_while_handling'></a>
```cs
opts.ListenToSqsQueue("slow-work", q => q.VisibilityTimeout = 60)
    .ProcessInline()
    // GH-4019: keep the messages of each received batch invisible while their
    // handlers run, extending by the visibility timeout every half timeout
    .ExtendVisibilityWhileHandling();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/AWS/Wolverine.AmazonSqs.Tests/Samples/Bootstrapping.cs' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_sqs_extend_visibility_while_handling' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

`ExtendVisibilityWhileHandling()` (inline endpoints only — Durable and Buffered have already
deleted the message by the time a handler runs) issues a `ChangeMessageVisibilityBatch` for
every message of the batch that is still in flight at each half-timeout tick, so a batch that
finishes inside half the timeout costs no extra API calls at all. Each message is kept
invisible for at most 12 hours from its receipt (the SQS limit; lower it with the optional
`maximum` argument), after which Wolverine stops extending and logs a warning. It is opt-in in
6.x because it adds billable API calls under sustained slow handling and changes when a
crashed node's in-flight messages reappear.

- **NativeAck** endpoints hold the message longest of all — for lane queue time *plus* handler
  time — and that queue time is unbounded by design. Renewal there is therefore **mandatory and
  does not consult `ExtendVisibilityWhileHandling()`**: an opt-in default-false flag would mean
  "off by default" for the one mode that cannot survive it being off. `MaximumVisibilityExtension`
  is still the ceiling, and idle lanes still cost nothing. See
  [Native Ack Endpoints](/guide/messaging/listeners#native-ack-endpoints) for what happens when a
  visibility timeout is lost anyway.

## The send side: batch API, one batch in flight

Wolverine sends with `SendMessageBatch` (10 messages per API call, the SQS maximum) through its
batched sender. Two defaults are worth revisiting for high-volume publishers:

```cs
opts.PublishMessage<OrderPlaced>().ToSqsQueue("orders")
    // Default is 1: only one batch API call in flight at a time per endpoint.
    // Raising this is the single cheapest send-throughput lever for SQS.
    .MessageBatchMaxDegreeOfParallelism(8)

    // The batch timeout is a debounce (each new message resets it) —
    // for low-rate latency-sensitive routes, shrink it from the 250ms default.
    .MessageBatchTimeout(50.Milliseconds());
```

With the defaults, sustained sending tops out around 10 messages per SQS round trip per
endpoint. Since SQS bills per API call, batching efficiency is also directly a cost lever.

## Payload size

The default envelope mapper embeds the serialized Wolverine envelope in the message body as
Base64, which inflates the wire size by roughly a third — budget against the 256 KB SQS message
limit accordingly. For large or high-volume payloads where you control both ends, the raw JSON
mapper (or a custom `ISqsEnvelopeMapper`) avoids the Base64 wrapping.

SQS also caps an entire `SendMessageBatch` *request* at 256 KB, not just each message in it, so
ten individually legal 30 KB messages would bounce the whole request. Wolverine chunks outgoing
batches on both limits — ten entries and the request payload budget — so large messages simply
produce smaller batches.

## FIFO queues

FIFO queues give broker-side ordering per `MessageGroupId` (mapped automatically from
Wolverine's `Envelope.GroupId`), but two caveats:

1. FIFO throughput is limited **per message group** — total throughput scales with the number
   of distinct group ids, so a workload funneled into a few groups will hit SQS's FIFO caps
   long before the transport is the bottleneck.
2. Broker-side ordering does not by itself serialize *processing*: Buffered/Durable endpoints
   execute a received batch in parallel. Pair FIFO listening with
   `PartitionProcessingByGroupId(...)` (or Inline mode) to preserve per-group ordering
   end to end.

On standard queues, `PartitionProcessingByGroupId(...)` alone gives per-key sequential
processing within a node without the FIFO throughput caps, and
`UseShardedAmazonSqsQueues(...)` in a global partitioned topology adds cluster-wide ordering
at the cost of forced durable endpoints.

Native scheduled delivery via `DelaySeconds` is used automatically for delays up to 15 minutes
on standard queues; longer delays or FIFO queues fall back to Wolverine's database-backed
scheduling.

## Interpreting Wolverine's metrics

`wolverine-execution-time` measures the handler *plus all middleware* (including time blocked
inside middleware); `wolverine-effective-time` is wall-clock from the sender's `SentAt` stamp
through handling, cascading-message flush, and completion, and is sensitive to clock skew
across machines.
