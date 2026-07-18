# Scheduled Delivery

Amazon SQS supports delaying the delivery of individual messages through the
[per-message `DelaySeconds` parameter](https://docs.aws.amazon.com/AWSSimpleQueueService/latest/SQSDeveloperGuide/sqs-message-timers.html),
but only up to a hard limit of **15 minutes**, and only on **standard** (non-FIFO) queues.

Wolverine uses this native capability automatically whenever it can. There is nothing to enable —
just use Wolverine's normal scheduled sending:

```cs
// Delivered through SQS native DelaySeconds (standard queue, within 15 minutes)
await bus.ScheduleAsync(new ValidateOrder(orderId), 5.Minutes());

// Also fine — an absolute time within the next 15 minutes
await bus.ScheduleAsync(new ValidateOrder(orderId), DateTimeOffset.UtcNow.AddMinutes(10));
```

The decision between native delay and Wolverine's own message scheduling is made **per message**
at the time the message is routed:

| Scenario | Behavior |
|----------|----------|
| Standard queue, delay ≤ 15 minutes | Sent immediately to SQS with `DelaySeconds` — the broker holds the message and delivers it after the delay |
| Standard queue, delay > 15 minutes | Falls back to Wolverine's own [message scheduling](/guide/messaging/message-bus.html#scheduling-message-delivery-or-execution) — the message is sent to SQS when its time arrives |
| FIFO queue, any delay | Always falls back to Wolverine's scheduled message storage — SQS only supports a queue-level delay on FIFO queues, never per-message delays |

A couple of important consequences of the native path:

* **Storage-less applications** (no message store configured) can now use short, retry-style
  scheduled sends to standard SQS queues reliably — the broker itself holds the message, so no
  database is needed and the delay survives an application restart.
* Because the message is handed to SQS immediately, a natively delayed message **cannot be
  cancelled or rescheduled** once sent.

For delays past the 15 minute limit (and for all FIFO queues), the fallback behavior depends on
your durability setup:

* With a message store configured, the scheduled message is kept in the durable inbox and
  published to SQS by Wolverine's scheduling agent when the time comes — this is fully durable.
* Without a message store, the message is held by the in-process, in-memory scheduler, and will
  be lost if the application restarts before the scheduled time.
