# Dead Letter Queues

It's an imperfect world, and sooner or later one of your message handlers is going to throw an exception
it can't recover from -- a malformed message, a downstream system that never comes back, or just plain bad
data. When Wolverine decides that a message can *never* succeed, it stops retrying and sets the message aside
in a **dead letter queue** (DLQ) so that it's out of the way but not lost. You can come back later, fix whatever
was wrong, and replay it.

This tutorial walks through the whole story: how a message ends up in the DLQ, *where* it actually goes
(it depends on your transport), and how to inspect and replay dead lettered messages.

::: tip
This page ties together two reference areas. For the full error-handling API see [Error Handling](/guide/handlers/error-handling),
and for the database-backed storage details see [Dead Letter Storage](/guide/durability/dead-letter-storage).
:::

## When does a message get dead lettered?

A message lands in the dead letter queue when Wolverine has exhausted every retry/requeue option you've configured
for the exception that was thrown. By default, with no error policies at all, Wolverine allows **3 attempts**
before giving up. After that, the message is moved to the dead letter queue.

The set of actions Wolverine can take on a failure is:

| Action               | Description                                                                                   |
|----------------------|-----------------------------------------------------------------------------------------------|
| Retry                | Immediately retry the message inline                                                           |
| Retry with Cooldown  | Wait a short time, then retry inline                                                           |
| Requeue              | Put the message at the back of the line for the receiving endpoint                            |
| Schedule Retry       | Retry the message at a later time                                                             |
| Discard              | Log, then drop the message -- never retried, never dead lettered                              |
| Move to Error Queue  | Send the message to the dead letter queue and stop                                            |
| Pause the Listener   | Stop processing on the listener for a set duration                                            |

The key thing to understand is the *default*: if you don't explicitly `Discard()` a message, Wolverine will
eventually send it to a dead letter queue once it runs out of retries. Dead lettering is the safety net, not
something you usually have to opt into.

## A minimal setup

Let's make a handler fail and watch the message get dead lettered. Here's a handler that always throws for a
particular bad input:

```cs
public record ProcessOrder(int OrderId, string PaymentMethod);

public class ProcessOrderHandler
{
    public void Handle(ProcessOrder command)
    {
        if (command.PaymentMethod == "invalid")
        {
            throw new InvalidPaymentException("The payment method provided is invalid.");
        }

        // ... otherwise process the order normally
    }
}
```

Some failures are transient and worth retrying; others are hopeless and should go straight to the DLQ. You
express that with error-handling policies. To retry a few times with a back-off and *then* dead letter the
message if it never recovers, reach for `RetryWithCooldown` -- a message that exhausts the cooldown attempts is
dead lettered automatically:

<!-- snippet: sample_exponential_backoff -->
<a id='snippet-sample_exponential_backoff'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        // Retry the message again, but wait for the specified time
        // The message will be dead lettered if it exhausts the delay
        // attempts
        opts
            .OnException<SqlException>()
            .RetryWithCooldown(50.Milliseconds(), 100.Milliseconds(), 250.Milliseconds());
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/Runtime/Samples/error_handling.cs#L145-L157' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_exponential_backoff' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

For an exception that you *know* can never succeed, skip the retries entirely and move the message straight to
the error queue:

<!-- snippet: sample_send_to_error_queue -->
<a id='snippet-sample_send_to_error_queue'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        // Don't retry, immediately send to the error queue
        opts.OnException<TimeoutException>().MoveToErrorQueue();
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/Runtime/Samples/error_handling.cs#L103-L111' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_send_to_error_queue' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The `MoveToErrorQueue()` action lives on Wolverine's error-policy fluent interface. You can declare these
rules globally on `opts.Policies`, per message type with a static `Configure(HandlerChain)` method, or with
attributes on the handler. The attribute form looks like this:

<!-- snippet: sample_configuring_error_handling_with_attributes -->
<a id='snippet-sample_configuring_error_handling_with_attributes'></a>
```cs
public class AttributeUsingHandler
{
    [ScheduleRetry(typeof(IOException), 5)]
    [RetryNow(typeof(SqlException), 50, 100, 250)]
    [RequeueOn(typeof(InvalidOperationException))]
    [MoveToErrorQueueOn(typeof(DivideByZeroException))]
    [MaximumAttempts(2)]
    public void Handle(InvoiceCreated created)
    {
        // handle the invoice created message
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/Runtime/Samples/error_handling.cs#L242-L256' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_configuring_error_handling_with_attributes' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

::: tip
If a message is genuinely invalid and could *never* be processed -- as opposed to "failing for now" -- prefer
`Discard()` over the dead letter queue so it doesn't pile up. See [Error Handling](/guide/handlers/error-handling).
:::

## Where does the message actually go?

This is the part that trips people up, because there are *two* different dead letter mechanisms and which one
Wolverine uses depends on your transport.

### 1. The database-backed dead letter table

If you've configured [durable message storage](/guide/durability/) (PostgreSQL, SQL Server, Marten, etc.) and you're
using the local queues -- or a transport that doesn't (yet) support native dead lettering -- Wolverine moves the
failed message into a `wolverine_dead_letters` table in your database. Each row is a `DeadLetterEnvelope` and
captures the failure context: the message type, the originating endpoint (`ReceivedAt`), the `ExceptionType` and
`ExceptionMessage`, timestamps, a `Replayable` flag, and the full serialized `Envelope` plus the deserialized
`Message` body.

Because these dead letters live in your own database, you can browse and manage them with SQL, the CLI, a REST
API, or the programmatic API -- all covered below.

### 2. Native transport dead letter queues

For brokers that have their own dead-letter concept, Wolverine uses *that* by default instead of the database
table:

- **RabbitMQ** declares a `wolverine-dead-letter-queue` and wires up the native
  [dead letter exchange](https://www.rabbitmq.com/dlx.html) on your queues. See
  [RabbitMQ Dead Letter Queues](/guide/messaging/transports/rabbitmq/deadletterqueues).
- **Amazon SQS** routes to a `wolverine-dead-letter-queue` SQS queue by default. See
  [SQS Dead Letter Queues](/guide/messaging/transports/sqs/deadletterqueues).
- Azure Service Bus, GCP Pub/Sub, NATS, Kafka, Redis, and Pulsar similarly use their native dead-letter facilities.

The trade-off: native transport DLQs are managed by the broker, so the database-backed tooling in the next
section (the `storage` CLI, the REST API, the SQL `replayable` trick) does **not** see them. If you'd rather have
*everything* land in the queryable database table, you can opt out of the native behavior. For RabbitMQ:

<!-- snippet: sample_disable_rabbit_mq_dead_letter_queue -->
<a id='snippet-sample_disable_rabbit_mq_dead_letter_queue'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        // Disable dead letter queueing by default
        opts.UseRabbitMq()
            .DisableDeadLetterQueueing()

            // or conventionally
            .ConfigureListeners(l =>
            {
                // Really does the same thing as the first usage
                l.DisableDeadLetterQueueing();
            });

        // Disable the dead letter queue for this specific queue
        opts.ListenToRabbitQueue("incoming").DisableDeadLetterQueueing();
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/RabbitMQ/Wolverine.RabbitMQ.Tests/Samples.cs#L469-L489' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_disable_rabbit_mq_dead_letter_queue' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

and the equivalent for SQS is `DisableAllNativeDeadLetterQueues()`. With native dead-lettering disabled, Wolverine
falls back to the persisted `wolverine_dead_letters` table.

## Inspecting and replaying dead letters

Everything in this section applies to the **database-backed** dead letter table. (To inspect a *native* transport
DLQ, use that broker's own tooling.)

### From the command line

If you've opted into JasperFx command-line parsing (`await app.RunJasperFxCommands(args)`), Wolverine adds a
`storage` command. To see how many messages are sitting in the dead letter table:

```bash
dotnet run -- storage counts
```

The output includes a "Dead Letter" row alongside Incoming, Outgoing, Scheduled, and Handled counts.

Once you've fixed whatever caused the failures, replay the dead lettered messages back into active processing:

```bash
dotnet run -- storage replay
```

You can narrow the replay to a single exception type with the `--exception-type` (`-t`) flag:

```bash
dotnet run -- storage replay --exception-type InvalidPaymentException
```

Replaying doesn't reprocess the messages on the spot -- it marks them `replayable` in the table. Wolverine's
durability agent then moves them back into the incoming table on its next polling cycle, so recovery is
near-real-time rather than instantaneous.

### With plain SQL

Under the hood, replaying simply flips a flag. You can do exactly the same thing by hand:

```sql
update wolverine_dead_letters set replayable = true where exception_type = 'InvalidPaymentException';
```

The durability agent picks these rows up and moves them back into active incoming message handling.

### From the REST API

Wolverine ships an HTTP API for dead letter management in the `WolverineFx.Http` package. Register it in your
application startup:

<!-- snippet: sample_register_dead_letter_endpoints -->
<a id='snippet-sample_register_dead_letter_endpoints'></a>
```cs
app.MapDeadLettersEndpoints()

    // It's a Minimal API endpoint group,
    // so you can add whatever authorization
    // or OpenAPI metadata configuration you need
    // for just these endpoints
    //.RequireAuthorization("Admin")
    ;
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Program.cs#L292-L302' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_register_dead_letter_endpoints' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

That gives you three endpoints:

- `POST /dead-letters/` -- query/page through dead letters, filtered by message type, exception type, date range, tenant, etc.
- `POST /dead-letters/replay` -- mark specific envelope ids as replayable.
- `DELETE /dead-letters/` -- permanently delete specific dead letters.

See [Dead Letter Storage](/guide/durability/dead-letter-storage) for the full request/response shapes.

### Programmatically

The same operations are available in code through the `IDeadLetters` service hanging off your `IMessageStore`:

```cs
var store = host.Services.GetRequiredService<IMessageStore>();

// Mark everything that failed with a particular exception type as replayable
await store.DeadLetters.MarkDeadLetterEnvelopesAsReplayableAsync("InvalidPaymentException");
```

`IDeadLetters` also exposes `QueryAsync`, `SummarizeAllAsync`, `ReplayAsync`, `DiscardAsync`, and
`EditAndReplayAsync` (which lets you fix up a message body before replaying it) for more fine-grained control.

## Keeping the dead letter table from growing forever

A busy system can accumulate a lot of dead letters, and an oversized table will eventually drag on performance.
Dead letter expiration is off by default (for backwards compatibility), but you can opt in so Wolverine expires
and expunges old dead letters automatically:

<!-- snippet: sample_enabling_dead_letter_queue_expiration -->
<a id='snippet-sample_enabling_dead_letter_queue_expiration'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {

        // This is required
        opts.Durability.DeadLetterQueueExpirationEnabled = true;

        // Default is 10 days. This is the retention period
        opts.Durability.DeadLetterQueueExpiration = 3.Days();

    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/BootstrappingSamples.cs#L40-L53' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_enabling_dead_letter_queue_expiration' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Wolverine uses the message's `DeliverBy` value as the expiration if it has one; otherwise it adds the configured
`DeadLetterQueueExpiration` to the current time. Expired rows are deleted by background processes, so cleanup is
not quite real time.

## Where to go next

- [Error Handling](/guide/handlers/error-handling) -- the full set of retry, requeue, discard, and pause policies that decide *when* a message is dead lettered, plus exception filtering and the circuit breaker.
- [Dead Letter Storage](/guide/durability/dead-letter-storage) -- the database table, expiration, and the complete Dead Letters REST API reference.
- [Managing Message Storage](/guide/durability/managing) -- the broader `storage` and `resources` CLI tooling.
- [RabbitMQ Dead Letter Queues](/guide/messaging/transports/rabbitmq/deadletterqueues) and [SQS Dead Letter Queues](/guide/messaging/transports/sqs/deadletterqueues) -- native transport DLQ configuration.
