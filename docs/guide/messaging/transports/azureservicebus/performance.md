# Performance Tuning

This page collects the levers that matter most for throughput and latency with the Azure
Service Bus transport, and the factors behind them.

## The receive side

Buffered and Durable endpoints (the default is buffered) pull messages in batches of
`MaximumMessagesToReceive` (default **20**) per receive call, waiting up to `MaximumWaitTime`
(default 5 seconds). Durable endpoints write each received batch to the database inbox in a
**single** batched insert, which makes durable ASB endpoints comparatively cheap per message.
Message *settlement* (complete) is one service call per message.

### Prefetch <Badge type="tip" text="6.21" />

`PrefetchCount` lets the Service Bus client stream messages ahead of your receive calls and is
the single biggest receive-throughput lever — without it, a listener's ceiling is roughly one
batch per network round trip:

```cs
// Transport-wide default
opts.UseAzureServiceBus(connectionString).PrefetchCount(100);

// Or per endpoint
opts.ListenToAzureServiceBusQueue("orders")
    .PrefetchCount(60)
    .ListenerCount(2)
    .MaximumParallelMessages(10);
```

A good starting point is 2–3× `MaximumMessagesToReceive` × `ListenerCount`. **Do not set
prefetch higher than what your workers can settle within the queue's lock duration**:
prefetched messages age against their locks while waiting client-side, and an expired lock
means silent redelivery and a rising delivery count.

### Inline endpoints process one message at a time by default

Inline ASB endpoints use a `ServiceBusProcessor`, whose `MaxConcurrentCalls` defaults to **1**.
Wolverine does not change that default, so an inline listener is single-threaded unless you
raise it:

```cs
opts.ListenToAzureServiceBusQueue("orders")
    .ProcessInline()
    .ConfigureProcessor(o => o.MaxConcurrentCalls = 10);
```

## Lock duration vs. processing window

For Buffered/Durable endpoints, Wolverine does not renew message locks while messages wait in
the local worker queue. If (buffered backlog × handler time ÷ `MaximumParallelMessages`) can
exceed the queue's lock duration, locks expire silently and the broker redelivers:

- **Durable** endpoints deduplicate redelivery through the inbox (wasted work, no duplicate
  side effects).
- **Buffered** endpoints settle messages as soon as they are buffered — so lock expiry is moot
  for them, but an ungraceful crash loses the buffered backlog (at-most-once on crash).
- **Inline** endpoints can rely on the processor's automatic lock renewal
  (`ConfigureProcessor(o => o.MaxAutoLockRenewalDuration = ...)`, SDK default 5 minutes).

Keep `BufferingLimits` sized so the backlog clears within the lock duration, or lengthen the
queue's `LockDuration`.

## The send side

Wolverine batches outgoing messages into real `ServiceBusMessageBatch`es, respecting the
broker's size limits (256 KB per message on Standard, 1 MB on Premium). Two defaults to
revisit for high-volume publishers:

```cs
opts.PublishMessage<OrderPlaced>().ToAzureServiceBusQueue("orders")
    // Default 1: one batch in flight at a time per endpoint.
    .MessageBatchMaxDegreeOfParallelism(4)

    // The batch timeout is a debounce (each new message resets it) —
    // shrink it for low-rate, latency-sensitive routes.
    .MessageBatchTimeout(50.Milliseconds());
```

Inline sending (and every internal requeue/retry path) sends one message per service call —
prefer batched sending for high-volume routes. When publishing to *partitioned* entities,
outgoing batches are additionally grouped by session id so each batch shares a partition key.

## Sessions and ordered processing

Sessions give broker-enforced ordering per `SessionId` (mapped automatically from Wolverine's
`Envelope.GroupId`) with cluster-wide exclusivity — but session processing is inherently more
expensive than plain consumption: each session must be accepted, locked, drained, and released.
Keep `RequireSessions(n)` counts modest, and note that strict per-session *processing* order on
Buffered/Durable endpoints also needs `PartitionProcessingByGroupId(...)` (or inline
execution), since the local worker queue otherwise executes a session's batch in parallel —
this pairing is what `ExclusiveNodeWithSessionOrdering(...)` sets up for you.

When you need per-key ordering but not broker-enforced cross-node exclusivity, a non-session
queue with `PartitionProcessingByGroupId(...)` is significantly cheaper. For cluster-wide
partitioned ordering without sessions, `UseShardedAzureServiceBusQueues(...)` in a global
partitioned topology spreads groups across N queues with exclusive listeners (forced durable —
budget for the inbox writes).

## Namespace tier and client options

Standard vs. Premium changes message size limits (256 KB vs. 1 MB), latency consistency, and
throughput headroom — benchmark on the tier you will run. The transport uses AMQP over TCP by
default; use the client-options hook on `UseAzureServiceBus(...)` to configure web sockets,
proxies, or `ServiceBusRetryOptions` (`TryTimeout`, retry counts and delays) when operating
through restrictive networks.

## Interpreting Wolverine's metrics

`wolverine-execution-time` measures the handler *plus all middleware* (including time blocked
inside middleware); `wolverine-effective-time` is wall-clock from the sender's `SentAt` stamp
through handling, cascading-message flush, and settlement, and is sensitive to clock skew
across machines.
