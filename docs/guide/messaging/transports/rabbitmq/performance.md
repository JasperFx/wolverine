# Performance Tuning

This page collects the levers that matter most for throughput and latency with the Rabbit MQ
transport, and the factors behind them.

## Rabbit MQ listeners default to Inline — and that shapes everything

Unlike most Wolverine transports, Rabbit MQ endpoints default to **`Inline`** mode: each message
is processed completely — handler, cascading messages, acknowledgement — before the next message
is dispatched on that channel. Combined with the client's default dispatch concurrency of 1,
an out-of-the-box Rabbit MQ listener consumes **one message at a time**. This gives strong
ordering and no-loss semantics, but it means `MaximumParallelMessages` has **no effect** on an
Inline endpoint. If a listener seems mysteriously slow, check its mode first.

Three ways to scale consumption:

```cs
opts.ListenToRabbitQueue("orders")
    // 1. Multiple parallel listeners = N channels + N consumers on the queue.
    //    Works for every mode, and is the way to scale Inline endpoints.
    .ListenerCount(5)

    // 2. Switch to buffered (or durable) mode so messages fan out to
    //    parallel in-process workers (MaximumParallelMessages wide).
    .BufferedInMemory()

    .MaximumParallelMessages(10);

// 3. Raise the client's per-channel dispatch concurrency (applies transport-wide)
opts.UseRabbitMq(...).ConfigureChannelCreation(o => o.ConsumerDispatchConcurrency = 4);
```

## Choosing the endpoint mode

- **Inline** (default): ack after successful handling. Safest, slowest per listener; scale with
  `ListenerCount`.
- **Buffered**: messages are acked **as soon as they are buffered in memory**, before handling —
  an ungraceful shutdown loses whatever was buffered (at-most-once on crash). Fastest mode;
  use with idempotent handlers or tolerable-loss workloads.
- **Durable**: each message is written to the database inbox before it is acked, and handled
  from there. The consumer coalesces prefetched deliveries for up to 5ms into a single batched
  inbox insert (up to `MaximumMessagesToReceive`, default 100), so under load the write cost is
  paid per batch rather than per message — measured locally this took a 2,000 msg/s stream from
  unbounded backlog to sub-millisecond delivery p50, and nearly tripled the maximum sustained
  durable rate (GH-3492). Set `MaximumMessagesToReceive(1)` for strict message-at-a-time
  persistence. Database write latency still bounds the ceiling; scale with `ListenerCount` and
  keep the inbox database close.

## Prefetch

Wolverine sets the channel prefetch (`basic.qos`) automatically: for Buffered/Durable endpoints
it defaults to `2 × MaximumParallelMessages`, and for Inline endpoints to 100. Override with
`.PreFetchCount(...)` on the listener. For Inline endpoints prefetch mostly just hides network
latency between messages; for Buffered/Durable it controls how far the broker can run ahead of
your workers — higher values smooth throughput at the cost of more unacked messages redelivered
if the node dies.

## Publisher confirms are off by default

Wolverine publishes with confirms **disabled** by default: publishes are fire-and-forget at the
AMQP level, which is fast, but a broker-side failure after the publish call can lose the
message. Enabling confirms (`ConfigureChannelCreation(o => o.PublisherConfirmationsEnabled = true)`)
makes every publish await the broker's acknowledgement — a per-publish round trip. Pick one
durability mechanism deliberately: if you are already using Wolverine's durable outbox, the
outbox provides the delivery guarantee and confirms mostly add latency; if you run without the
outbox and cannot lose messages, turn confirms on and accept the cost. Also note Wolverine
publishes with `mandatory: false`, so a message routed to a non-existent queue binding is
silently dropped by the broker — provision your topology (or use `AutoProvision`) rather than
relying on publish failures to surface binding mistakes.

## Back pressure closes the channel

When a Buffered/Durable listener exceeds `BufferingLimits.Maximum` (default 1,000 in-memory
messages), Wolverine stops the listener — which for Rabbit MQ tears down the channel, and the
broker **redelivers everything unacked** when the listener restarts below the resume threshold
(default 500). Under sustained overload this becomes a redelivery churn cycle. Size
`BufferingLimits` so normal bursts fit, or add processing parallelism so the buffer drains
faster than it fills.

## Queue types

Classic queues are the throughput baseline. Quorum queues add Raft replication — real fsync and
inter-node traffic per message — so expect materially lower per-queue throughput and benchmark
with your actual cluster. Also be aware that RabbitMQ 4.x defaults quorum queues to a
`delivery-limit` of 20: the *broker* will dead-letter or drop a message after 20 deliveries
regardless of Wolverine's own error policies, so align your retry policies with the queue's
delivery limit.

## Sequential processing by key

Rabbit MQ has no broker-native partition/session primitive surfaced through Wolverine, so
ordered-by-key processing is done on the consumer side:

- `PartitionProcessingByGroupId(...)` on a listener keeps messages with the same `GroupId`
  strictly sequential while different groups process in parallel — prefer this over blocking
  locks inside handlers/middleware, which tie up worker slots and inflate execution-time
  metrics.
- `UseShardedRabbitQueues(...)` inside a global partitioned topology spreads messages across N
  queues by group hash with exclusive listeners for cluster-wide ordering — note global
  partitioning forces durable endpoints, so it carries the inbox-write cost per message.

## Interpreting Wolverine's metrics

`wolverine-execution-time` measures the handler *plus all middleware* (including time blocked
inside middleware); `wolverine-effective-time` is wall-clock from the sender's `SentAt` stamp
through handling, cascading-message flush, and the final ack, and is sensitive to clock skew
across machines.
