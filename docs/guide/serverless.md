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
| Sending endpoints | Forced to *Inline* | Made *Durable* where the transport can be |
| Transactional outbox | Off | On |
| Recovers messages that failed to send | No | Yes, through an externally triggered durability pass |
| Scheduled messages | Nothing releases them | Released by the durability pass |
| Extra moving parts | None | Something has to trigger the durability pass |

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
2. After the transaction commits, Wolverine sends every outgoing message right then and there, inside the same request, and waits for each
   send to complete. There's no background sending agent to hand things off to
3. Wolverine deletes the outbox rows for every message that was sent successfully in one batch
4. If a send fails, Wolverine logs the failure and leaves that row in the outbox for the next durability pass to pick up

That works today with the Marten, Polecat, Fisher, EF Core, and RavenDb integrations, and the durability pass is supported by the PostgreSQL,
SQL Server, MySQL, Oracle, SQLite, RavenDb, and Cosmos DB message stores. That includes [multi-tenanted](/guide/durability/managing.html) message
databases, where the durability pass works its way through every tenant database.

::: info
Every sending endpoint that can be durable is made durable under this mode, so you don't have to mark endpoints with `UseDurableOutbox()`
yourself. Endpoints for transports that can't be durable fall back to *Inline*.
:::

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

## The Durability Pass

Since there's no background durability agent in `ServerlessWithOutbox`, *something* outside of your function has to periodically
tell Wolverine to catch up. We call that a "durability pass," and one pass:

* Sends the stale outbox messages whose inline send failed
* Releases scheduled messages that are due, including scheduled retries from error handling
* Recovers incoming messages that were persisted to the inbox but never handled
* Deletes expired handled envelopes, dead letters, and deduplication records

A durability pass is bounded by a maximum duration (30 seconds by default) and a batch size (`DurabilitySettings.RecoveryBatchSize` by default),
so it fits comfortably inside a function invocation. Each message store takes a non-blocking lock before doing any work, so if two passes
overlap, the second one just skips the stores the first one is already working on instead of waiting.

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
// so and you can just go around again
while (report.HasMoreWork && !report.TimedOut)
{
    report = await host.RunDurabilityPassAsync(maxDuration: 30.Seconds());
}

Console.WriteLine($"Sent {report.OutgoingSent} outgoing messages, released {report.ScheduledReleased} scheduled messages");
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/DurabilityModes.cs#L111-L123' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_running_a_durability_pass' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The `DurabilityPassReport` that comes back tells you what the pass did for each message store and each step, and whether or not there's
still more work to be done.

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

1. Maps the request into an envelope. A request that doesn't carry a message gets a 400
<!-- DRAFT (PR #5044 review, for Jeremy to word): a request can now carry several envelopes (a Wolverine-batched Pub/Sub message, or the HTTP transport's envelope batch). They run in order, the request is acked only if all were, and the first one that asks for a retry stops the rest so the broker redelivers the whole request. Suggest recommending DeduplicateWithInbox for batching publishers. -->
2. Runs the message handler inline inside the HTTP request, including the handler's normal [error handling policies](/guide/handlers/error-handling)
3. Sends every outgoing message before writing the response
4. Uses the status code to tell the broker what happened. A message that was handled successfully, discarded, or moved to Wolverine's dead letter
   storage gets a 200. If the error policies asked for a retry, or an exception escaped the policies altogether, the receiver answers with a 500 so the broker
   redelivers the message with its own backoff

Just like the durability endpoint, mapping a push receiver requires an authorization decision through its `PushReceiverOptions`, either
`opts.RequireAuthorization("policy")` or an explicit `opts.AllowAnonymous()`, or the mapping throws an exception.

Both status codes can be changed through the `PushReceiverOptions` configuration as well. Since push delivery is at-least-once, you can also set
`DeduplicateWithInbox` to `true` to have the push receiver skip any message id that is already in the durable inbox, and record every message it acks
as handled.

<!-- DRAFT (PR #5044 review, for Jeremy to word): WolverineEnvelopePushMapper now also reads the binary/wolverine-envelopes batch form, and one disallowed message type rejects the whole batch with a 400. -->
Wolverine ships a `WolverineEnvelopePushMapper` out of the box that reads a whole binary Wolverine envelope out of the request body. Since the caller
writes the entire envelope, you have to tell that mapper which message types it's allowed to accept, and anything else gets a 400. The mapper also
throws away any reply, acknowledgement, or destination information the caller put on the envelope:

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

## Pre-Generate All Types

The runtime code generation that Wolverine does comes with a potentially non-trivial "cold start" problem with its first
usage. In serverless architectures, that's probably intolerable. With Wolverine, you can bypass that cold start problem
by opting into [pre-generated types](/guide/codegen.html#generating-code-ahead-of-time).

## What Doesn't Work in Serverless

Just so you know going in, neither `Serverless` nor `ServerlessWithOutbox` supports:

* Local queues. Use `InvokeAsync()` for in-process work instead, or send messages to an external broker
* Listeners on external brokers. See [Receiving Messages by Push](#receiving-messages-by-push) instead
* Recurring scheduled messages. Use your cloud provider's scheduler to call an HTTP endpoint instead
* Anything that depends on leader election or agent assignment, like the [Marten async daemon in distributed mode](/guide/durability/marten/distribution)
