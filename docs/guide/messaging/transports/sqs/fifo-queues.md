# FIFO Queues

[Amazon SQS FIFO queues](https://docs.aws.amazon.com/AWSSimpleQueueService/latest/SQSDeveloperGuide/FIFO-queues.html) guarantee that messages are processed exactly once, in the exact order they are sent. Wolverine has built-in support for SQS FIFO queues.

## Naming Convention

Wolverine detects FIFO queues by the `.fifo` suffix on the queue name — this follows the AWS naming requirement. Simply use a queue name ending in `.fifo` when configuring your endpoints:

```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.UseAmazonSqsTransportLocally()
            .AutoProvision();

        opts.PublishMessage<OrderPlaced>()
            .ToSqsQueue("orders.fifo")
            .ConfigureQueueCreation(request =>
            {
                // Required for FIFO queues
                request.Attributes[QueueAttributeName.FifoQueue] = "true";

                // Enable content-based deduplication so you don't have to
                // supply a DeduplicationId on every message
                request.Attributes[QueueAttributeName.ContentBasedDeduplication] = "true";
            });

        opts.ListenToSqsQueue("orders.fifo", queue =>
        {
            queue.Configuration.Attributes[QueueAttributeName.FifoQueue] = "true";
            queue.Configuration.Attributes[QueueAttributeName.ContentBasedDeduplication] = "true";
        });
    }).StartAsync();
```

## Message Group Id and Deduplication Id

SQS FIFO queues use a **Message Group Id** to determine ordering — messages within the same group are delivered in order. Optionally, a **Message Deduplication Id** prevents duplicate delivery within a 5-minute window.

When Wolverine sends messages to a FIFO queue, it automatically maps:

- `Envelope.GroupId` &rarr; SQS `MessageGroupId`
- `Envelope.DeduplicationId` &rarr; SQS `MessageDeduplicationId`

You can set these values using `DeliveryOptions` when publishing:

```cs
await messageBus.PublishAsync(new OrderPlaced(orderId), new DeliveryOptions
{
    GroupId = orderId.ToString(),
    DeduplicationId = $"order-placed-{orderId}"
});
```

If you enable `ContentBasedDeduplication` on the queue (as shown above), you can omit the `DeduplicationId` and SQS will generate one based on the message body.

## Dead Letter Queues for FIFO

When using dead letter queues with FIFO queues, the dead letter queue must also be a FIFO queue. Make sure to name it with a `.fifo` suffix:

```cs
opts.ListenToSqsQueue("orders.fifo", queue =>
{
    queue.Configuration.Attributes[QueueAttributeName.FifoQueue] = "true";
    queue.Configuration.Attributes[QueueAttributeName.ContentBasedDeduplication] = "true";
}).ConfigureDeadLetterQueue("orders-errors.fifo", dlq =>
{
    dlq.Attributes[QueueAttributeName.FifoQueue] = "true";
    dlq.Attributes[QueueAttributeName.ContentBasedDeduplication] = "true";
});
```

## Partitioned Publishing with FIFO Queues

For high-throughput scenarios where you need ordered processing *per group* but want parallelism *across groups*, consider using Wolverine's [partitioned sequential messaging](/guide/messaging/partitioning) with sharded SQS FIFO queues. This distributes messages across multiple queues based on their group id, giving you the best of both worlds — strict ordering within a group and horizontal scaling across groups.
