# Performance Tuning

This page collects the levers that matter most for throughput and latency with the Amazon SQS
transport, and the factors behind them.

## The receive side: batches in, singles out

Wolverine receives from SQS in batches — each poll asks for up to `MaxNumberOfMessages`
(default **10**, the SQS maximum) with long polling enabled by default (`WaitTimeSeconds` = 5).
Durable endpoints benefit doubly: the whole received batch is written to the database inbox in
a **single** batched insert, so durable SQS endpoints are considerably cheaper per message than
push-based transports.

Message *completion*, however, is currently one `DeleteMessage` API call per message — ten
deletes per receive at full batches. At high throughput, delete round trips (not receives) are
usually the ceiling for a single listener. The practical levers:

```cs
opts.ListenToSqsQueue("orders")
    // Parallel pollers: N independent receive loops on the same queue.
    // The main receive-side throughput lever today.
    .ListenerCount(4)

    // Long-poll duration. Raise toward 20s for low-traffic queues to cut
    // empty-receive API calls (and cost); keep short only if you need
    // faster listener shutdown.
    .ConfigureListener(l => l.WaitTimeSeconds = 20)

    .MaximumParallelMessages(10);
```

## Visibility timeout: size it against your processing window

Wolverine sets each received message's visibility timeout at receive (default **120 seconds**)
and does **not** extend it while messages wait in the local worker queue or execute. If
(queued messages ÷ processing rate) + handler time can exceed the visibility timeout, SQS
redelivers messages that are still in flight:

- **Durable** endpoints deduplicate redeliveries through the inbox — you pay wasted work, not
  duplicate side effects.
- **Buffered** endpoints delete messages from SQS *as soon as they are buffered*, before
  handling — so instead of duplicates you get at-most-once semantics: an ungraceful crash
  loses the buffered backlog.
- **Inline** endpoints delete after successful handling — safest, and the visibility timeout
  only needs to cover a single handler execution.

Rule of thumb: keep `BufferingLimits.Maximum × average handler time ÷ MaximumParallelMessages`
comfortably under the visibility timeout, or raise the timeout on the queue.

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
