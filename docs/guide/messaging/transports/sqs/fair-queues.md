# Fair Queues

[Amazon SQS fair queues](https://docs.aws.amazon.com/AWSSimpleQueueService/latest/SQSDeveloperGuide/using-messagegroupid-property.html) let a **standard** queue mitigate "noisy neighbor" problems in multi-tenant workloads. By assigning a `MessageGroupId` to each message, SQS spreads dwell time more fairly across groups so that one tenant generating a large backlog does not starve the others. Unlike FIFO queues, fair queues imply **no ordering or deduplication semantics** — they keep standard queue throughput while improving fairness.

## Enabling Fair Queues

Wolverine already maps `Envelope.GroupId` to the SQS `MessageGroupId` for [FIFO queues](/guide/messaging/transports/sqs/fifo-queues). For a standard queue this mapping is opt-in via `EnableFairQueueMessageGroups()`, so existing standard queues are unaffected unless you ask for it:

```cs
opts.PublishMessage<OrderPlaced>()
    .ToSqsQueue("orders")
    .EnableFairQueueMessageGroups();
```

Once enabled, set the group id the same way you would for a FIFO queue — typically a tenant id — through `DeliveryOptions`:

```cs
await messageBus.PublishAsync(new OrderPlaced(orderId), new DeliveryOptions
{
    GroupId = tenantId
});
```

The group id can also be assigned with [message partitioning](/guide/messaging/partitioning) rather than per-publish.

::: tip
`EnableFairQueueMessageGroups()` only affects standard queues. FIFO queues (names ending in `.fifo`) always map `MessageGroupId` and `MessageDeduplicationId` regardless of this setting.
:::

## Customizing the Group Id

The group id is derived through the endpoint's `ISqsEnvelopeMapper` via `DetermineGroupId(Envelope)`, which returns `Envelope.GroupId` by default. A custom mapper can override it to source the group id from a header, the message body, or a tenant id when interoperating with non-Wolverine systems.
