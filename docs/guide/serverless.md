# Wolverine and Serverless

Wolverine was very much originally envisioned for usage in long running processes, and as such, wasn't initially well suited to
serverless technologies like [Azure Functions](https://azure.microsoft.com/en-us/products/functions), [AWS Lambda functions](https://aws.amazon.com/pm/lambda),
or [Google Cloud Run](https://cloud.google.com/run). A normal Wolverine application leans on background processes for quite a bit
of its work -- node registration and heartbeats, leader election, the durability agent that recovers the transactional inbox and outbox,
polling for scheduled messages, and listeners pulling messages off of a broker. None of that is going to work out very well in a process
that might only live as long as a single HTTP request, or that gets its CPU throttled the second the response goes out.

If you're choosing to use Wolverine HTTP endpoints or message handling as part of a serverless function, here's what we'd suggest:

1. Pick one of the two serverless optimized durability modes, either `Serverless` or `ServerlessWithOutbox`. More on the difference
   between those two below
2. Absolutely take advantage of [pre-generated types](/guide/codegen.html#generating-code-ahead-of-time) to cut down the all important cold start problem with serverless functions
3. If you are using `ServerlessWithOutbox`, set up something outside of your function to periodically trigger a "durability pass"
4. If you need to receive messages from a broker, have the broker push them to your function over HTTP instead of trying to listen

## Which Mode Do I Want?

You've got two choices:

| | `Serverless` | `ServerlessWithOutbox` |
|---|---|---|
| Node registration, leader election, background agents | Off | Off |
| Local queues | Removed | Removed |
| Messages handled by this application | Dropped, so use `InvokeAsync()` | Stored in the inbox and handled by the next durability pass |
| Sending endpoints | Forced to *Inline* | Made *Durable* where the transport can be |
| Transactional outbox | Off | On |
| Recovers messages that failed to send | No | Yes, through an externally triggered durability pass |
| Scheduled messages | Nothing releases them | Stored in the inbox and released by the durability pass when they're due |
| Extra moving parts | None | Something has to trigger the durability pass |

That "messages handled by this application" row deserves a little explanation. Neither mode has local queues, but under `ServerlessWithOutbox`
a message that has a handler in this very same application -- whether you `PublishAsync()` or `SendAsync()` it, cascade it from another handler,
or route it there yourself with an explicit `PublishMessage<T>().ToLocalQueue()` rule -- isn't just dropped on the floor. Wolverine writes it to
the durable inbox, owned by "any node," inside the handler's transaction when there is one, and the next durability pass picks it up and handles it.
So you get the atomicity of the outbox, but the message runs as soon as the next pass gets to it rather than inside the request. Plain `Serverless`
drops those messages, so use `InvokeAsync()` there if you need the work done. If your application calls `DisableConventionalLocalRouting()` itself,
that still turns off the "handled by this application" routing in either mode, exactly like it does everywhere else.

The same goes for scheduled messages under `ServerlessWithOutbox`. `ScheduleAsync()` or `DelayedFor()` on a message this application handles, the
`TimeoutMessage`s from your sagas, and scheduled sends to a transport that can't schedule natively like TCP or RabbitMQ -- including
`IMessageBus.EndpointFor()` sends with a `ScheduleDelay` -- all go into the inbox owned by any node, and the durability pass releases them when
they come due.

The short version is that `Serverless` is the simplest possible thing. If a handler commits its transaction and then the
outgoing message fails to send, that message is just gone. If that's acceptable for your system, or you aren't sending
any messages to a broker from your serverless functions in the first place, use `Serverless`.

If you need the guarantee that a message you cascade from a handler will be sent sooner or later if and only if the
handler's transaction succeeds -- and that's really the whole point of a [transactional outbox](/guide/durability/) -- use `ServerlessWithOutbox`.

## Serverless Mode

Let's say that you want to use the transactional middleware for either Marten or EF Core within your serverless functions, but
you really don't care about the outbox. That's all good, but you will want to turn off all of Wolverine's transactional inbox/outbox
functionality and background processing with this setting:

<!-- snippet: sample_configuring_the_serverless_mode -->
<a id='snippet-sample_configuring_the_serverless_mode'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.Services.AddMarten("some connection string")

            // This adds quite a bit of middleware for
            // Marten
            .IntegrateWithWolverine();

        // You want this maybe!
        opts.Policies.AutoApplyTransactions();

        // But wait! Optimize Wolverine for usage within Serverless
        // and turn off the heavy duty, background processes
        // for the transactional inbox/outbox
        opts.Durability.Mode = DurabilityMode.Serverless;
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/DurabilityModes.cs#L13-L33' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_configuring_the_serverless_mode' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

In this mode, every endpoint that Wolverine sends to is forced to be *Inline*, meaning that messages are sent immediately without
any background processing as would be normal with *Buffered* or *Durable* endpoints. You can also say that explicitly in your
configuration, and it's harmless to do so:

<!-- snippet: sample_usage_of_send_inline -->
<a id='snippet-sample_usage_of_send_inline'></a>
```cs
.UseWolverine(opts =>
{
    opts.UseRabbitMq().AutoProvision().AutoPurgeOnStartup();
    opts
        .PublishAllMessages()
        .ToRabbitQueue(queueName)

        // This option is important inside of Serverless functions
        .SendInline();
})
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/RabbitMQ/Wolverine.RabbitMQ.Tests/Bugs/Bug_189_fails_if_there_are_many_messages_in_queue_on_startup.cs#L20-L32' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_usage_of_send_inline' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

::: warning
If you've configured any sending endpoints with `UseDurableOutbox()`, or used `UseDurableOutboxOnAllSendingEndpoints()`, the
`Serverless` mode quietly downgrades all of those endpoints to *Inline* and you lose the outbox guarantee for messages sent to them.
Wolverine logs one warning at startup listing every endpoint it downgraded. If you see that warning, you probably wanted
`ServerlessWithOutbox` instead.
:::

## Serverless with the Transactional Outbox

`DurabilityMode.ServerlessWithOutbox` gives you the same lean runtime as `Serverless` -- no node registration, no leader election,
no local queues, no listeners started for you, and no background timers -- but keeps Wolverine's transactional outbox in place:

<!-- snippet: sample_configuring_the_serverless_with_outbox_mode -->
<a id='snippet-sample_configuring_the_serverless_with_outbox_mode'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.Services.AddMarten("some connection string")
            .IntegrateWithWolverine();

        opts.Policies.AutoApplyTransactions();

        // The same lean runtime as Serverless, but outgoing
        // messages still go through the transactional outbox
        opts.Durability.Mode = DurabilityMode.ServerlessWithOutbox;

        // Optional. This is already 60 seconds under ServerlessWithOutbox
        opts.Durability.OutboxStaleTime = 1.Minutes();

        // Optional. Adds an index on the outgoing envelope table's
        // timestamp column for the durability pass to use
        opts.Durability.EnableOutgoingTimestampIndex = true;
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/DurabilityModes.cs#L85-L106' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_configuring_the_serverless_with_outbox_mode' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Here's what happens when a handler cascades a message in this mode:

1. The outgoing message is persisted to the outbox inside the very same database transaction as the rest of the handler's work. The outbox row
   is marked as being owned by "any node" because there's no node record in this mode to own it
2. After the transaction commits, Wolverine sends every outgoing message right then and there, inside the same request, and waits for the
   sends to complete. There's no background sending agent to hand things off to. The messages are grouped by destination, and each group goes
   out in as few round trips to the broker as its transport allows, in batches of the endpoint's `MessageBatchSize`. Different destinations
   are sent concurrently, but each destination keeps its order
3. Wolverine deletes the outbox rows for the messages the broker actually accepted. If a transport only accepts part of a batch -- and SQS, SNS,
   and Azure Service Bus can all do that -- only the rows for the rest are left behind
4. If a send fails, Wolverine logs the failure and leaves that row in the outbox for a later durability pass. The failure also restarts the
   row's stale clock, so the row goes to the back of the line and isn't tried again until `OutboxStaleTime` has passed again, which gives you
   a natural backoff for free. A message the broker refuses as a serialization failure -- say an oversized SQS message without
   `FragmentOversizedMessages()` -- is never going to send no matter how many times it's retried, so Wolverine moves it to dead letter storage
   instead of retrying it forever. Kafka is the one exception there, because an oversized Kafka record comes back as an ordinary failure and
   keeps on being retried

Those steps assume the handler has a database transaction. A message sent with no transaction at all -- from a Wolverine.HTTP endpoint that
isn't using transactional middleware, or just a plain `IMessageBus.PublishAsync()` call -- is sent inline the same way before the call returns.
Its outbox row is written first and owned by any node, so if that send fails, a later durability pass recovers it.

That works today with the Marten, Polecat, Fisher, EF Core, and RavenDb integrations, and the durability pass is supported by the PostgreSQL,
SQL Server, MySQL, Oracle, SQLite, RavenDb, and Cosmos DB message stores. That includes [multi-tenanted](/guide/durability/managing.html) message
databases, where the durability pass works its way through every tenant database.

::: info
Every sending endpoint that can be durable is made durable under this mode, so you don't have to mark endpoints with `UseDurableOutbox()`
yourself. Endpoints for transports that can't be durable fall back to *Inline*.
:::

::: warning
The durability mode wins over anything you say about an individual endpoint. Under `ServerlessWithOutbox`, an endpoint you explicitly configured
with `SendInline()` is still made durable, and under plain `Serverless`, one you configured with `UseDurableOutbox()` is still downgraded to *Inline*.
Wolverine logs a warning at startup naming each endpoint whose explicit mode it overrode. The same rule applies to listeners, so under plain
`Serverless` a partitioned listener with an explicit mode other than *Inline* fails at startup rather than quietly running in a mode you didn't ask for.
:::

### RabbitMQ Publisher Confirms

An outbox row is deleted as soon as its inline send returns, so under `ServerlessWithOutbox` Wolverine has to be sure the broker really has the
message before that happens. With RabbitMQ that means Wolverine turns on publisher confirms with confirmation tracking for every channel, regardless
of what your `ConfigureChannelCreation()` says, and publishes to queues as *mandatory*. A publish only counts once the broker has confirmed it, and
publishing to a queue that doesn't exist fails the send instead of silently dropping the message. That costs you one confirmed round trip per message.
Publishing to an exchange with no bindings still drops the message, just like it does in every other mode, because that's just how RabbitMQ works.

Under both serverless modes Wolverine also skips declaring its system reply queue and doesn't stamp a reply uri on outgoing messages, because
nothing would ever be listening for the reply anyway.

### OutboxStaleTime

`DurabilitySettings.OutboxStaleTime` defaults to 60 seconds under `ServerlessWithOutbox`. A value you set explicitly always wins.

The stale time matters here because a durability pass will only try to recover an outbox row once it's older than the stale time. That
keeps a durability pass from racing a request that is still in the middle of sending that very message. 60 seconds lines up with Google Cloud
Scheduler's one minute minimum and with typical broker SDK publish timeouts. You'd only get a duplicate message out of this if an inline send hangs
for longer than the stale time, and Wolverine's at-least-once delivery already allows for that anyway.

### InboxStaleTime

`ServerlessWithOutbox` makes your listening endpoints durable too, and a durable listener stamps each inbox row with the number of the process
that received it before it acknowledges the message to the broker. If that process dies after the acknowledgement but before the handler finishes,
nothing else would ever claim that row. To cover that, `DurabilitySettings.InboxStaleTime` defaults to 5 minutes under `ServerlessWithOutbox`,
and a durability pass recovers any inbox row whose stale clock has run out, along with the rows that no process ever claimed. Again, a value you
set explicitly always wins.

Make sure the stale time is longer than your longest running handler, or a durability pass can start a second copy of a message that's still
being handled. The relational message stores support this. RavenDb and Cosmos DB don't have an inbox stale clock, so a pass only recovers the
inbox rows that no process ever claimed with those two stores.

### The Outgoing Timestamp Index

To find the outbox rows whose inline send failed, the durability pass queries the outgoing envelope table by its `timestamp` column. If you
expect a lot of rows in that table, you can opt into an index on that column with `DurabilitySettings.EnableOutgoingTimestampIndex`
as shown in the sample above. That's off by default so that upgrading Wolverine doesn't change your database schema unless you
ask it to. Every relational message store supports the index. RavenDb builds an auto index for that query on its own and Cosmos DB indexes every property by default, so the flag doesn't change anything for those two stores.

### Moving an Existing Database to ServerlessWithOutbox

That `timestamp` column isn't always there. The relational message stores only add a `timestamp` column to the outgoing
envelope table when `OutboxStaleTime` has a value, and to the incoming envelope table when `InboxStaleTime` has a value.
Since `ServerlessWithOutbox` gives both of those a default, switching an existing application over to this mode is a
schema change to your message storage, even if you never touch either setting yourself.

If Wolverine is allowed to migrate the message storage at startup, you won't notice anything. But a lot of folks
running in serverless functions turn that off with `AutoBuildMessageStorageOnStartup = AutoCreate.None` to keep
migrations out of the cold start, and that's exactly the case to watch out for. With `AutoCreate.None`, Wolverine
only logs a warning at startup that the message storage is out of date and leaves your schema alone. The durability pass
reads and updates that `timestamp` column to find and recover the stale outbox rows, so that part of the pass fails every
single time until the schema is migrated, and the messages whose inline send failed are never recovered.

The fix is to migrate the message storage as part of your deployment, before the new version of the function takes any
traffic. `dotnet run -- resources setup` always applies the message storage migration, even when `AutoCreate` is `None`.
See [Disable Automatic Storage Migration](/guide/durability/managing#disable-automatic-storage-migration) for more on that.

## The Durability Pass

Since there's no background durability agent in `ServerlessWithOutbox`, *something* outside of your function has to periodically
tell Wolverine to catch up. We call that a "durability pass," and one pass:

* Sends the stale outbox messages whose inline send failed
* Releases scheduled messages that are due, including scheduled retries from error handling
* Recovers incoming messages that were persisted to the inbox but never handled, including the messages this application handles itself
* Deletes expired handled envelopes, dead letters, and deduplication records

A few details about the outgoing messages that fail again during a pass. A row whose send fails during the pass has its stale clock restarted,
so it waits another `OutboxStaleTime` before it's tried again, and rows that keep on failing don't hold back the healthy rows behind them. A message
the broker can never accept is moved to dead letter storage, and the pass report counts those under `OutgoingDeadLettered`. Lastly, a store that was
skipped because another pass already held its lock counts as more work left (`HasMoreWork`), because nothing in that pass can say the store had
nothing waiting.

A durability pass is bounded by a maximum duration (30 seconds by default) and a batch size (`DurabilitySettings.RecoveryBatchSize` by default),
so it fits comfortably inside a function invocation. Each message store takes a non-blocking lock before doing any work, so if two passes
overlap, the second one just skips the stores the first one is already working on instead of waiting.

### Behind a Transaction-Mode Connection Pooler

With PostgreSQL, that lock is a session-level advisory lock on a dedicated connection. If your function reaches
PostgreSQL through a connection pooler in transaction mode, like PgBouncer with `pool_mode = transaction` or Cloud SQL
managed connection pooling, a session-level lock isn't reliable. The pooler only keeps your client on one server
connection for the length of a transaction, so the lock can be taken on one server connection and the unlock sent to
another. The lock then stays held on the first one, and later passes skip that store, or overlap, until the pooler
closes that server connection.

In that case, opt into a transaction-scoped lock instead:

<!-- snippet: sample_transaction_scoped_durability_pass_lock -->
<a id='snippet-sample_transaction_scoped_durability_pass_lock'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        // A connection string that goes through PgBouncer or another
        // pooler in transaction mode
        opts.Services.AddMarten("some connection string")
            .IntegrateWithWolverine();

        opts.Durability.Mode = DurabilityMode.ServerlessWithOutbox;

        // Take the durability pass lock inside a transaction that stays
        // open for the pass, so it is taken and released on the same
        // server connection. Don't use this with Marten's async daemon
        opts.Durability.UseTransactionScopedDurabilityPassLock = true;
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/DurabilityModes.cs#L111-L128' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_transaction_scoped_durability_pass_lock' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

With `UseTransactionScopedDurabilityPassLock`, Wolverine opens a transaction on the dedicated lock connection, takes
`pg_try_advisory_xact_lock` inside it, keeps that transaction open while the pass runs, and rolls it back when the pass
is done, even if a step fails. Ending the transaction releases the lock on the same server connection that took it. The
pass steps still use their own connections, just like before.

If you leave the session-level lock on behind a transaction-mode pooler, Wolverine logs a warning whenever releasing
the lock finds that the server connection it reached doesn't hold it, and that warning points at this setting.

::: warning
The lock connection sits idle in an open transaction for as long as the pass runs, which is why this is off by default.
Marten's async daemon treats any session with an open transaction older than an event sequence gap as a possible
reserver of that gap, so asynchronous projections can't get past a gap that was first seen while a pass is running.
That includes a gap the daemon only discovers during the pass, so a custom `IDurabilityPassStep` that waits for
asynchronous projections to catch up will wait on its own pass until the pass runs out of time. Wolverine logs a warning
the first time a pass runs extension steps under this lock. Only use this option if you don't run Marten's async daemon
against the same database, or if that wait is acceptable to you.

Wolverine turns off the server's `idle_in_transaction_session_timeout` for the lock transaction alone, with `SET LOCAL`.
It can't do the same for PgBouncer's `idle_transaction_timeout`, so if you set that one, make it longer than a pass. If
the lock transaction is ended early anyway, the pass reports an error when it releases the lock, because another pass
may have run at the same time.

Only the PostgreSQL message store honors this setting today. Any other store keeps its usual lock, and the pass logs a
warning that the setting is ignored.
:::

::: warning
A durability pass only runs under `DurabilityMode.ServerlessWithOutbox`, and all three ways of triggering one refuse to run in any other mode. In
the other modes Wolverine's own durability agents are already doing this work, and a pass running beside them could send or handle a message twice.
:::

There are three different ways to trigger a durability pass.

### Directly in Code

If you're writing your own timer triggered function or job, you can call `RunDurabilityPassAsync()` on the `IHost`:

<!-- snippet: sample_running_a_durability_pass -->
<a id='snippet-sample_running_a_durability_pass'></a>
```cs
// Run one bounded pass of durability work across every message store
var report = await host.RunDurabilityPassAsync(maxDuration: 30.Seconds());

// If the pass ran out of time or hit the batch size, it tells you
// so and you can go around again. Cap the number of passes so that a
// store another pass is holding the lock on, or a backlog bigger than
// one invocation, can't keep this function alive forever
var passes = 1;
while (report.HasMoreWork && !report.TimedOut && passes < 5)
{
    report = await host.RunDurabilityPassAsync(maxDuration: 30.Seconds());
    passes++;
}

Console.WriteLine($"Sent {report.OutgoingSent} outgoing messages, dead lettered {report.OutgoingDeadLettered}, released {report.ScheduledReleased} scheduled messages");
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/DurabilityModes.cs#L133-L149' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_running_a_durability_pass' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The `DurabilityPassReport` that comes back tells you what the pass did for each message store and each step, which stores it skipped because
another pass held their lock (`SkippedStores`), and whether or not there's still more work to be done. `HasMoreWork` is true when the pass timed
out, when any step reported work left over, or when a store was skipped, so a loop like the one above goes around again on a skipped store too.
That's why you want a cap on the number of passes rather than looping until `HasMoreWork` is false.

### Over HTTP

If you're using [Wolverine.HTTP](/guide/http/), you can map an endpoint that something like Cloud Scheduler, Amazon EventBridge Scheduler,
or a timer triggered function can call:

<!-- snippet: sample_map_wolverine_durability_endpoints -->
<a id='snippet-sample_map_wolverine_durability_endpoints'></a>
```cs
// The pass sends messages and deletes rows, so mapping it takes an authorization
// decision: a policy, or AllowAnonymous() when Cloud Run IAM or a function key
// already protects the application
app.MapWolverineDurabilityEndpoints(opts => opts.RequireAuthorization("scheduler"));
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/Wolverine.Http.Tests/durability_pass_triggers_5016.cs#L66-L73' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_map_wolverine_durability_endpoints' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

That maps `POST /_wolverine/durability/pass`. You can override the URL prefix as an argument to `MapWolverineDurabilityEndpoints()`, and
bound the pass with the optional `maxDuration` (in seconds) and `batchSize` query string values like `POST /_wolverine/durability/pass?maxDuration=20&batchSize=200`.
Callers can't ask for more than `DurabilityEndpointOptions.MaxBatchSize` (1,000 by default) or `DurabilityEndpointOptions.MaxDuration` (5 minutes by default),
and larger values are just lowered to those caps. The response is a JSON report of the pass with a 200 status code if every step succeeded, or a 500
if any step failed so that your scheduler records the failure. A failed step only reports the exception type, and the details go to your application's log.
If the application isn't running in `ServerlessWithOutbox`, the endpoint answers with a 409 and doesn't run anything.

::: warning
This endpoint sends messages and deletes rows from your database, so `MapWolverineDurabilityEndpoints()` makes you decide how it's protected and
throws an exception if you don't. Either name the authorization policy it requires with `opts.RequireAuthorization("scheduler")` (or
`opts.RequireAuthorization()` for the default policy), or call `opts.AllowAnonymous()` when something in front of your application like Cloud Run
IAM or a function key already decides who can call it.
:::

### From the Command Line

Lastly, the `storage flush` command starts up your application, runs one durability pass, prints out a table of what it did, and shuts down. That's
a good fit for a Cloud Run Job or a Kubernetes `CronJob`:

```bash
dotnet run -- storage flush --max-duration 30 --batch-size 500
```

Both flags are optional (`-d` and `-b` for short). If any step fails, the command fails as well so that your job runner reports it.

## Receiving Messages by Push

Neither serverless mode starts any listeners for you, because a serverless function isn't going to sit around polling a broker. Instead, you can have
the broker push messages to your function over HTTP -- Google Pub/Sub push subscriptions, SNS or EventBridge to an HTTP endpoint, Azure Event Grid, and
the like -- and use Wolverine.HTTP's push receiver to handle them.

`MapWolverinePushReceiver()` takes an `IPushEnvelopeMapper` that knows how to turn the incoming HTTP request into a Wolverine envelope:

<!-- snippet: sample_map_wolverine_push_receiver -->
<a id='snippet-sample_map_wolverine_push_receiver'></a>
```cs
// The mapper is the transport's: it turns the request the broker pushes into a Wolverine envelope.
// Mapping the receiver takes an authorization decision, here a policy that validates the push
// subscription's OIDC token
app.MapWolverinePushReceiver("/_wolverine/push/orders", new FakePubsubPushMapper(),
    opts => opts.RequireAuthorization("pubsub-push"));
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/Wolverine.Http.Tests/Transport/push_receiver_5016.cs#L91-L99' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_map_wolverine_push_receiver' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

For each request, the push receiver:

1. Maps the request into one or more envelopes. A request that doesn't carry a message gets a 400
2. Runs the message handler inline inside the HTTP request, including the handler's normal [error handling policies](/guide/handlers/error-handling).
   If the request carried several envelopes -- a Wolverine-batched Pub/Sub message, say, or the HTTP transport's own envelope batch -- they run in order
3. Sends every outgoing message before writing the response
4. Uses the status code to tell the broker what happened. A message that was handled successfully, discarded, or moved to Wolverine's dead letter
   storage gets a 200. If the error policies asked for a retry, or an exception escaped the policies altogether, the receiver answers with a 500 so the broker
   redelivers the message with its own backoff. With several envelopes in one request, the request is only acknowledged if every one of them was, and
   the first envelope that asks for a retry stops the rest so that the broker redelivers the whole request

::: tip
If your publisher batches messages into one push request, turn on `DeduplicateWithInbox` as described below so that a redelivery of the whole
request doesn't run the messages that already succeeded a second time.
:::

Just like the durability endpoint, mapping a push receiver requires an authorization decision through its `PushReceiverOptions`, either
`opts.RequireAuthorization("policy")` or an explicit `opts.AllowAnonymous()`, or the mapping throws an exception.

Both status codes can be changed through the `PushReceiverOptions` configuration as well.

### Delivery Attempts and Dead Letter Policies

Every redelivery from the broker arrives as a brand new envelope as far as Wolverine can tell, so the error handling policies that count attempts --
`Requeue(n)`, `ScheduleRetry()`, `RetryTimes(n)` followed by `MoveToErrorQueue()`, `MaximumAttempts` -- can only escalate when the broker tells
Wolverine which delivery this is. The push mapper sets `Envelope.Attempts` from the broker's delivery count when the request carries one. For Pub/Sub
that's the top level `deliveryAttempt` field, and Pub/Sub only sends that when the push subscription has a dead-letter policy. Without one, every
redelivery looks like attempt number one, the policy keeps answering with the retry status, and a poison message is redelivered until the broker
gives up on its own -- or forever.

So our recommendation is to configure a dead-letter policy, meaning a dead letter topic plus a maximum number of delivery attempts, on every push
subscription. It's what makes `deliveryAttempt` show up in the first place, and it's your backstop if Wolverine never acknowledges a message. Set
the broker's maximum delivery attempts higher than the number of attempts your Wolverine policy uses, or the broker dead letters the message before
Wolverine's own dead letter storage ever gets a look at it.

If you write your own `IPushEnvelopeMapper` for a broker that carries a delivery count, set `Envelope.Attempts = brokerDeliveryCount - 1` the same way.

### Deduplicating with the Inbox

Push delivery is at-least-once, so you can also set `DeduplicateWithInbox` to `true` to have the push receiver use the durable inbox to recognize
redeliveries. Before the handler runs, the receiver claims the message id in the inbox. A redelivery of a message that was already acknowledged
is acknowledged again without running the handler. A redelivery that shows up while the first delivery is still running -- which usually means your
handler is slower than the push subscription's ack deadline, since the broker redelivers at every deadline and a late 200 doesn't acknowledge
anything -- is answered with the retry status instead of running the handler a second time. Once the handler succeeds, the message is recorded as
handled.

Your push handlers should still finish well inside the ack deadline. Every one of those concurrent redeliveries costs you a request and a 500, and
with a dead-letter policy in place, each one counts as a delivery attempt.

The claim is an incoming inbox row owned by the process that received it, so a durability pass leaves it alone until `InboxStaleTime` (5 minutes by
default under `ServerlessWithOutbox`) runs out. That means `InboxStaleTime` has to be longer than your longest push handler. The upside is that if
the process dies in the middle of a request, a later pass recovers and runs the message, and any redeliveries after that are acknowledged as duplicates.
A retry outcome deletes the claim so that the broker's redelivery gets to run the handler.

All of that costs up to three inbox round trips per message -- the handled check, the claim, and marking it handled -- instead of two.

Wolverine ships a `WolverineEnvelopePushMapper` out of the box that reads a whole binary Wolverine envelope out of the request body, or a whole batch
of them in the `binary/wolverine-envelopes` form that Wolverine's own [HTTP transport](/guide/http/transport) sends. Since the caller writes the entire
envelope, you have to tell that mapper which message types it's allowed to accept, and anything else gets a 400. One disallowed message type in a batch
rejects the whole batch. The mapper also throws away any reply, acknowledgement, or destination information the caller put on the envelope:

<!-- snippet: sample_map_wolverine_envelope_push_receiver -->
<a id='snippet-sample_map_wolverine_envelope_push_receiver'></a>
```cs
// A caller that pushes whole Wolverine envelopes only gets to send the message types named here
app.MapWolverinePushReceiver("/_wolverine/push/envelopes", new WolverineEnvelopePushMapper(typeof(PushedMessage)),
    opts => opts.RequireAuthorization("pubsub-push"));
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/Wolverine.Http.Tests/Transport/push_receiver_5016.cs#L101-L107' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_map_wolverine_envelope_push_receiver' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

### Google Pub/Sub Push Subscriptions

For Google Cloud Pub/Sub, the `WolverineFx.Pubsub.Http` Nuget adds a mapper for push subscriptions. It's a separate Nuget so that an application
that pulls from Pub/Sub the normal way doesn't take on a dependency on ASP.NET Core:

<!-- snippet: sample_map_pubsub_push_receiver -->
<a id='snippet-sample_map_pubsub_push_receiver'></a>
```cs
// Every push subscription can share one route: each pushed message is read by the Pub/Sub endpoint
// whose subscription the push request names. Mapping it takes an authorization decision: a policy that
// validates the push subscription's OIDC token, or AllowAnonymous() when Cloud Run IAM already
// protects the service, as here
_app.MapPubsubPushReceiver("/pubsub/push", opts => opts.AllowAnonymous());

// Or read everything pushed to a route through one Pub/Sub endpoint
_app.MapPubsubPushReceiver("/pubsub/orders", opts => opts.AllowAnonymous(),
    endpointUri: new Uri("pubsub://wolverine/orders"));
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/GCP/Wolverine.Pubsub.Tests/pubsub_push_mapper_5016.cs#L56-L68' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_map_pubsub_push_receiver' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Each pushed message is read by the Pub/Sub endpoint whose subscription the push request names, so any custom envelope mapper, interoperability
settings, or `DefaultIncomingMessage<T>()` that you've configured on that endpoint apply to pushed messages the same way they do to pulled messages.
Pub/Sub messages without a Wolverine message id get a stable id derived from the subscription and the Pub/Sub message id, so the inbox
deduplication still recognizes a redelivery.

::: warning
Wolverine does not create push subscriptions for you. The push subscription that the push receiver depends on -- its push endpoint pointing at this
route's public URL, the OIDC token and audience your authorization policy validates, the ack deadline, the retry policy, and the dead-letter policy
recommended above -- has to be provisioned outside of Wolverine with Terraform, `gcloud`, Pulumi, or whatever you use for the rest of your Google
Cloud infrastructure.

Be careful with `AutoProvision()` on a push-receiving `ServerlessWithOutbox` host too. It creates every `ListenToPubsubTopic()` subscription as a *pull*
subscription, in the default project and in every tenant project, and nothing ever pulls from those subscriptions in this mode. Wolverine leaves a
subscription that already exists alone, so creating the push subscription first under the name Wolverine would use is one workaround. The simpler one
is to not `AutoProvision()` the listener subscriptions on the push host at all. See [GH-5054](https://github.com/JasperFx/wolverine/issues/5054) for
where this is headed.
:::

## Provisioning Broker Topology

`AutoProvision()` is a great convenience for a long running process that starts up once and then runs for days. In a
serverless function though, every cold start is an application startup. With RabbitMQ for example, an application using
`AutoProvision()` declares every exchange, queue, and binding it knows about each time a new instance spins up, and those
are extra broker round trips sitting right in the middle of the cold start you're trying so hard to keep short.

Our recommendation is to leave `AutoProvision()` off inside your functions and to provision the broker topology outside of
them instead. That can be `dotnet run -- resources setup` run as a deployment step, which sets up the broker objects
whether or not `AutoProvision()` is turned on and migrates your message storage at the same time, or it can be Terraform,
Pulumi, or whatever you already use for the rest of your infrastructure.

Under `ServerlessWithOutbox` with RabbitMQ, this isn't just about performance. Since Wolverine publishes to queues as
*mandatory* in that mode, a send to a queue that doesn't exist yet fails, and the message waits in the outbox until a
durability pass runs after the queue finally does exist. So get the topology in place before the function takes traffic.

::: tip
Neither serverless mode starts any listeners, but the RabbitMQ transport still opens a separate listening connection by
default. Add `UseSenderConnectionOnly()` to your `UseRabbitMq()` configuration to skip that connection on every cold start.
:::

## Pre-Generate All Types

The runtime code generation that Wolverine does comes with a potentially non-trivial "cold start" problem with its first
usage. In serverless architectures, that's probably intolerable. With Wolverine, you can bypass that cold start problem
by opting into [pre-generated types](/guide/codegen.html#generating-code-ahead-of-time).

## What Doesn't Work in Serverless

Just so you know going in, neither `Serverless` nor `ServerlessWithOutbox` supports:

* Local queues. Under plain `Serverless` a message this application handles is dropped, so use `InvokeAsync()` for in-process work instead or send
  the message to an external broker. `ServerlessWithOutbox` replaces local queues with the inbox: a published or cascaded message this application
  handles is stored there and handled by the next durability pass, so expect latency up to your pass interval
* Listeners on external brokers. See [Receiving Messages by Push](#receiving-messages-by-push) instead. Provisioning the listening side of a broker
  still runs though, so `AutoProvision()` of Pub/Sub subscriptions creates pull subscriptions that nothing ever reads. The push subscriptions themselves
  are provisioned outside of Wolverine, as described under [Google Pub/Sub Push Subscriptions](#google-pub-sub-push-subscriptions)
* Recurring scheduled messages. Use your cloud provider's scheduler to call an HTTP endpoint instead
* Request/reply through a broker. `InvokeAsync<T>()` to a remote endpoint needs a listener to bring the reply back, and neither mode starts one, so
  Wolverine throws right away instead of running the remote handler and then waiting out the whole timeout. Request/reply over Wolverine's own
  [HTTP transport](/guide/http/transport) still works, because the reply comes back in the same HTTP exchange
* Anything that depends on leader election or agent assignment, like the [Marten async daemon in distributed mode](/guide/durability/marten/distribution)

