# Durable Messaging

::: tip
For a practical walkthrough of the transactional outbox pattern with both Marten and EF Core, see the blog post
[Build Resilient Systems with Wolverine's Transactional Outbox](https://jeremydmiller.com/2024/12/08/build-resilient-systems-with-wolverines-transactional-outbox/).
:::

Wolverine can integrate with several database engines and persistence tools for:

* Durable messaging through the transactional inbox and outbox pattern
* Transactional middleware to simplify your application code
* Saga persistence
* Durable, scheduled message handling
* Durable & replayable dead letter queueing
* Node and agent assignment persistence that is necessary for Wolverine to do agent assignments (its virtual actor capability)

## Transactional Inbox/Outbox

See the blog post [Transactional Outbox/Inbox with Wolverine and why you care](https://jeremydmiller.com/2022/12/15/transactional-outbox-inbox-with-wolverine-and-why-you-care/) for more context.

One of Wolverine's most important features is durable message persistence using your application's database for reliable "[store and forward](https://en.wikipedia.org/wiki/Store_and_forward)" queueing with all possible Wolverine transport options, including the [lightweight TCP transport](/guide/messaging/transports/tcp) and external transports like the [Rabbit MQ transport](/guide/messaging/transports/rabbitmq).

It's a chaotic world out when high volume systems need to interact with other systems. Your system may fail, other systems may be down,
there's network hiccups, occasional failures -- and you still need your systems to get to a consistent state without messages just
getting lost en route.

Consider this sample message handler from Wolverine's [AppWithMiddleware sample project](https://github.com/JasperFx/wolverine/tree/main/src/Samples/Middleware):

<!-- snippet: sample_debitaccounthandler_that_uses_imessagecontext -->
<a id='snippet-sample_debitaccounthandler_that_uses_imessagecontext'></a>
```cs
[Transactional]
public static async Task Handle(
    DebitAccount command,
    Account account,
    IDocumentSession session,
    IMessageContext messaging)
{
    account.Balance -= command.Amount;

    // This just marks the account as changed, but
    // doesn't actually commit changes to the database
    // yet. That actually matters as I hopefully explain
    session.Store(account);

    // Conditionally trigger other, cascading messages
    if (account.Balance > 0 && account.Balance < account.MinimumThreshold)
    {
        await messaging.SendAsync(new LowBalanceDetected(account.Id));
    }
    else if (account.Balance < 0)
    {
        await messaging.SendAsync(new AccountOverdrawn(account.Id), new DeliveryOptions{DeliverWithin = 1.Hours()});

        // Give the customer 10 days to deal with the overdrawn account
        await messaging.ScheduleAsync(new EnforceAccountOverdrawnDeadline(account.Id), 10.Days());
    }

    // "messaging" is a Wolverine IMessageContext or IMessageBus service
    // Do the deliver within rule on individual messages
    await messaging.SendAsync(new AccountUpdated(account.Id, account.Balance),
        new DeliveryOptions { DeliverWithin = 5.Seconds() });
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/Middleware/AppWithMiddleware/Account.cs#L121-L155' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_debitaccounthandler_that_uses_imessagecontext' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The handler code above is committing changes to an `Account` in the underlying database and potentially sending out additional messages based on the state of the `Account`. 
For folks who are experienced with asynchronous messaging systems who hear me say that Wolverine does not support any kind of 2 phase commits between the database and message brokers, 
you’re probably already concerned with some potential problems in that code above:

* Maybe the database changes fail, but there are “ghost” messages already queued that pertain to data changes that never actually happened
* Maybe the messages actually manage to get through to their downstream handlers and are applied erroneously because the related database changes have not yet been applied. That’s a race condition that absolutely happens if you’re not careful (ask me how I know 😦 )
* Maybe the database changes succeed, but the messages fail to be sent because of a network hiccup or who knows what problem happens with the message broker

What you need is to guarantee that both the outgoing messages and the database changes succeed or fail together, and that the new messages are not actually published until the database transaction succeeds. 
To that end, Wolverine relies on message persistence within your application database as its implementation of the [Transactional Outbox](https://microservices.io/patterns/data/transactional-outbox.html) pattern. Using the "outbox" pattern is a way to avoid the need for problematic
and slow [distributed transactions](https://en.wikipedia.org/wiki/Distributed_transaction) while still maintaining eventual consistency between database changes and the outgoing messages that are part of the logical transaction. Wolverine implementation of the outbox pattern
also includes a separate *message relay* process that will send the persisted outgoing messages in background processes (it's done by marshalling the outgoing message envelopes through [TPL Dataflow](https://docs.microsoft.com/en-us/dotnet/standard/parallel-programming/dataflow-task-parallel-library) queues if you're curious.)

If any node of a Wolverine system that uses durable messaging goes down before all the messages are processed, the persisted messages will be loaded from
storage and processed when the system is restarted. Wolverine does this through its [DurabilityAgent](https://github.com/JasperFx/wolverine/blob/main/src/Persistence/Wolverine.RDBMS/DurabilityAgent.cs) that will run within your application through Wolverine's
[IHostedService](https://docs.microsoft.com/en-us/aspnet/core/fundamentals/host/hosted-services?view=aspnetcore-6.0&tabs=visual-studio) runtime that is automatically registered in your system through the `UseWolverine()` extension method.

::: tip
Wolverine supports PostgreSQL, Sql Server, MySQL, SQLite, Oracle, RavenDb, and CosmosDB as the underlying message storage,
and [Marten](/guide/durability/marten), [Polecat](/guide/durability/polecat/), or
[Entity Framework Core](/guide/durability/efcore) as the application persistence framework.
:::

There are three things you need to enable for the transactional outbox (and inbox for incoming messages):

1. Set up message storage in your application, and manage the storage schema objects -- don't worry though, Wolverine comes with a lot of tooling to help you with that
2. Enroll outgoing subscriber or listener endpoints in the durable storage at configuration time
3. Enable Wolverine's transactional middleware or utilize one of Wolverine's outbox publishing services

The last bullet point varies a little bit between the [Marten integration](/guide/durability/marten) and the [EF Core integration](/guide/durability/efcore), so see the
the specific documentation on each for more details.


## Using the Outbox for Outgoing Messages

::: tip
It might be valuable to leave some endpoints as "buffered" or "inline" for message types that have limited lifetimes.
See the blog post [Ephemeral Messages with Wolverine](https://jeremydmiller.com/2022/12/20/ephemeral-messages-with-wolverine/) for an example of this.
:::

To make the Wolverine outbox feature persist messages in the durable message storage, you need to explicitly make the 
outgoing subscriber endpoints (Rabbit MQ queues or exchange/binding, Azure Service Bus queues, TCP port, etc.) be
configured to be durable.

That can be done either on specific endpoints like this sample:

<!-- snippet: sample_make_specific_subscribers_be_durable -->
<a id='snippet-sample_make_specific_subscribers_be_durable'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.PublishAllMessages().ToPort(5555)

            // This option makes just this one outgoing subscriber use
            // durable message storage
            .UseDurableOutbox();
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/PersistenceTests/Samples/DocumentationSamples.cs#L66-L77' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_make_specific_subscribers_be_durable' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Or globally through a built in policy:

<!-- snippet: sample_make_all_subscribers_be_durable -->
<a id='snippet-sample_make_all_subscribers_be_durable'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        // This forces every outgoing subscriber to use durable
        // messaging
        opts.Policies.UseDurableOutboxOnAllSendingEndpoints();
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/PersistenceTests/Samples/DocumentationSamples.cs#L52-L61' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_make_all_subscribers_be_durable' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

### Bumping out Stale Inbox/Outbox Messages <Badge type="tip" text="5.2" />

::: warning
Do **not** make the inbox timeout too low are you could accidentally make Wolverine try to replay messages that are happily floating
around in retries or just plain slow. Make the `InboxStaleTime` be at least longer than your longest expected message execution time
with a couple retries for good measure. Ask us how we know this is a potential problem...

Idempotency protections will help keep your system from having inconsistent state from accidentally having a message attempted to be handled multiple
times, but it's always best to not make your system work so hard.
:::

It should *not* be possible for there to be any path where a message gets "stuck" in the outbox tables without eventually
being sent by the originating node or recovered by a different node if the original node goes down first. However, it's 
an imperfect world. If you are using one of the relational backed message stores for Wolverine (PostgreSQL, SQL Server, MySQL, SQLite, or Oracle),
you can "bump" a persisted record in the `wolverine_outgoing_envelopes` to be recovered and sent by the outbox by
setting the `owner_id` field to zero.

::: info
Just be aware that opting into the `OutboxStaleTime` or `InboxStaleTime` threshold will require database changes through Wolverine's database
migration subsystem
:::

You also have this setting to force Wolverine to automatically "bump" and older messages that seem to be stalled in
the outbox table or the inbox table:

<!-- snippet: sample_configuring_outbox_stale_timeout -->
<a id='snippet-sample_configuring_outbox_stale_timeout'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        // Bump any persisted message in the outbox tables
        // that is more than an hour old to be globally owned
        // so that the durability agent can recover it and force
        // it to be sent
        opts.Durability.OutboxStaleTime = 1.Hours();
        
        // Same for the inbox, but it's configured independently
        // This should *never* be necessary and the Wolverine
        // team has no clue why this could ever happen and a message
        // could get "stuck", but yet, here this is:
        opts.Durability.InboxStaleTime = 10.Minutes();
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/PersistenceTests/Samples/DocumentationSamples.cs#L271-L288' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_configuring_outbox_stale_timeout' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Note that this will still respect the "deliver by" semantics. This is part of the polling that Wolverine normally does
against the inbox/outbox/node storage tables. Note that this will only happen if the setting above has a non-null
value.

## Using the Inbox for Incoming Messages

On the incoming side, external transport endpoint listeners can be enrolled into Wolverine's transactional inbox mechanics
where messages received will be immediately persisted to the durable message storage and tracked there until the message is
successfully processed, expires, discarded due to error conditions, or moved to dead letter storage.

To enroll individual listening endpoints or all listening endpoints in the Wolverine inbox mechanics, use
one of these options:

<!-- snippet: sample_configuring_durable_inbox -->
<a id='snippet-sample_configuring_durable_inbox'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.ListenAtPort(5555)

            // Make specific endpoints be enrolled
            // in the durable inbox
            .UseDurableInbox();

        // Make every single listener endpoint use
        // durable message storage
        opts.Policies.UseDurableInboxOnAllListeners();
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/PersistenceTests/Samples/DocumentationSamples.cs#L82-L97' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_configuring_durable_inbox' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

### Batched Inbox Writes <Badge type="tip" text="6.30" />

The durable inbox costs two database writes per message: the `INSERT` when the message arrives, and
the `UPDATE` that marks it handled when the handler finishes. Neither is issued one message at a time
under load. Wolverine coalesces both into short micro-batches:

- **Arrival**: transports that receive in batches (SQS, Azure Service Bus, Pub/Sub) hand the whole
  batch to the inbox in one round trip, and the one-at-a-time push transports (RabbitMQ, Kafka)
  accumulate deliveries for up to 5ms — or `MaximumMessagesToReceive` (default 100), whichever comes
  first — before one batched insert.
- **Completion**: concurrent handler completions share one batched mark-as-handled `UPDATE` (up to
  `DurabilitySettings.MarkAsHandledBatchSize` of them, default 100). One flush is in flight at a
  time and everything that completes while it runs joins the next one, so batches form from
  concurrency alone: a lone completion is flushed immediately, nothing waits on a timer, and every
  completion still waits for its own `UPDATE` to land before the message counts as handled — a
  tracked session finishing still means the inbox row is `Handled`. A batch that fails for any
  reason falls back to marking each message individually, with retries, so nothing is lost — only
  the round trip is shared. A message already marked handled inside a transactional middleware's own
  transaction is skipped.

The result is that under load the inbox cost is paid per batch rather than per message on both the
way in and the way out. `MaximumMessagesToReceive(1)` on a RabbitMQ or Kafka endpoint gives strict
one-at-a-time persistence on arrival; `opts.Durability.MarkAsHandledBatchSize = 1` does the same for
completion. The 6.x line only: none of this batching exists on 5.x, so a user reporting durable-inbox
overhead on 5.x may simply need to upgrade.

### Bounding Broker Redeliveries <Badge type="tip" text="6.30" />

There is one failure the inbox's own deduplication cannot get you out of on its own. Suppose a delivery
reaches Wolverine, is written to the inbox, and then the *settle* back to the broker fails — a dropped
connection, an expired lock, a channel that closed underneath the ack. The broker never heard the
acknowledgement, so it redelivers. The inbox correctly recognizes the redelivery as a duplicate and refuses
it, Wolverine tries to settle it again, that settle fails again, and the broker delivers it once more.

**Nothing counted in your process can stop that loop.** Every redelivery arrives as a brand new envelope,
so `Envelope.Attempts` and the ack-attempt budget both restart at zero each time around. The broker's own
delivery count is the only counter that survives the boundary, which is exactly why
`Envelope.BrokerDeliveryCount` exists.

`MaximumBrokerRedeliveries` puts a ceiling on it. Past the limit, an undeliverable duplicate is moved to the
dead letter queue instead of being settled one more time:

```csharp
opts.Durability.MaximumBrokerRedeliveries = 5;
```

The default is **0, meaning off** — the broker's own redelivery limit stays in charge and nothing changes.
That default is why this is additive for every existing application.

**It reads whatever native signal the transport already carries:**

| Transport | Signal | Always present? |
| --- | --- | --- |
| Azure Service Bus | `DeliveryCount` | Yes |
| Amazon SQS | `ApproximateReceiveCount` | Yes |
| RabbitMQ | the `x-death` count header | **No** — see below |
| Everything else | — | No signal, so the setting has no effect |

A transport that reports no count leaves `Envelope.BrokerDeliveryCount` null and is simply unaffected.
Wolverine does not guess a count from anything else.

::: warning Three things to know before you set it
**It is a delivery count, not a redelivery count**, despite the property name. The first delivery is `1` on
both Azure Service Bus and SQS, so `MaximumBrokerRedeliveries = 3` permits three deliveries and dead-letters
on the fourth. A message delivered exactly the permitted number of times still gets to run.

**RabbitMQ only reports a count after a dead-letter cycle.** The `x-death` header is written when a message
is rejected through a dead-letter exchange; a plain `nack`-with-requeue does not add one. So on a RabbitMQ
endpoint that is requeueing rather than dead-lettering, there is no count to bound and this setting does
nothing.

**It only applies where the loop actually turns** — the durable inbox's duplicate path. This is not a general
purpose retry limit, and it does not bound ordinary handler failures; that is what
[error handling policies](/guide/handlers/error-handling) are for. It also needs the listener to support a
native dead letter queue, since dead-lettering is the escape. Without one, Wolverine falls through to the
ordinary settle and behaves exactly as it did before.
:::

#### The in-process counterpart

`MaximumAckAttempts` (default `3`) bounds the *other* half of the same problem: how many times Wolverine
will retry a single settle before giving up and letting the broker redeliver.

The reason it is a budget carried on the envelope rather than a per-block retry count is that the durable
completion path stacks two retry blocks — the receiver's complete block, then the transport's own channel
callback — and each one bounded its own attempts independently. Their budgets multiplied rather than
combined, so what read as "three attempts" was really nine broker round trips, with neither block able to
see the other's count.

```csharp
opts.Durability.MaximumAckAttempts = 3;
```

The two are deliberately different tools. `MaximumAckAttempts` limits the effort spent on one delivery
inside one process; `MaximumBrokerRedeliveries` limits how many times a message may come back *around*
after that effort was abandoned. Neither one substitutes for the other, which is why both exist.

### Who Recovers the Inbox <Badge type="tip" text="6.22" />

An incoming envelope in durable storage is either owned by a specific node (its `owner_id` is that node's
assigned number) or it is *unowned* — `owner_id = 0`, meaning "any node may claim this". Messages become
unowned when the node that owned them dies ungracefully and another node releases its ownership, and when
replayed dead letter messages are moved back into the inbox. Getting those unowned messages back into a
running listener is what "inbox recovery" means.

Which node does that recovery depends on the endpoint's `ListenerScope`:

| Listener | Recovered by | Why |
| --- | --- | --- |
| `CompetingConsumers` (the default) | The **durability agent** for that message database | Every node is listening, so whichever node holds the database's durability agent can safely claim the messages and process them locally |
| `Exclusive` (`ExclusiveNodeWithParallelism()`, `ListenWithStrictOrdering()`) | The **node currently hosting the listener** | Only one node is listening. A different node claiming the messages would strand them again |
| `PinnedToLeader` (`ListenOnlyAtLeader()`) | The **node currently hosting the listener** | Same reason |

The distinction matters because the durability agent is **assigned per message database**, and those
assignments are distributed across the cluster completely independently of the listener agents. So the node
running the durability agent for a database is frequently *not* the node running that database's exclusive
listener. If the durability agent were in charge of recovery for an exclusive endpoint, the two agents would
deadlock: the agent would refuse to recover because its local listener isn't accepting, and the listening node
would never look. (That was [GH-3590](https://github.com/JasperFx/wolverine/issues/3590), fixed in 6.22.)

So for single node listeners, Wolverine inverts the ownership:

* The per-database durability agents **never claim** inbox messages for endpoints whose `ListenerScope` is not
  `CompetingConsumers`. They keep doing everything else for those endpoints — releasing a dead node's
  ownership, [bumping stale inbox rows](#bumping-out-stale-inbox-outbox-messages), expiring messages.
* The node that currently holds the listener recovers them itself, starting the moment the listener reaches
  `Accepting` and then re-checking on the `Durability.ScheduledJobPollingTime` cadence for as long as it stays
  `Accepting`. The repeat matters: a dead node's messages are released back to `owner_id = 0` later, by
  whichever node holds that database's durability agent, and that is usually *after* the exclusive listener has
  already restarted somewhere else.
* That sweep covers **every** database that can hold inbox rows for the listener: the main store, every tenant
  database when you use a separate database per tenant (including tenant databases provisioned at runtime), and
  any [ancillary stores](/guide/durability/marten/ancillary-stores).
* A listener that is latched, paused, or already at its `BufferingLimits` recovers nothing, exactly as with the
  durability agent — circuit breaking behaves the way it always has.

None of this is configurable, and it works the same in `Solo` mode as in `Balanced`. The practical consequence
worth remembering: **if no node in the cluster is running an exclusive endpoint's listener, that endpoint's
unowned inbox messages stay put by design.** They are recovered promptly once a listener activates. See
[Exclusive Node Processing](/guide/messaging/exclusive-node-processing#inbox-recovery-ownership).

## Local Queues

When you mark a [local queue](/guide/messaging/transports/local) as durable, you're telling Wolverine to ensure that every message published
to that queue be stored in the backing message database until it is successfully processed. Doing so makes even the local queues be able
to guarantee eventual delivery even if the current node where the message was published fails before the message is processed.

To configure individual or set durability on local queues by some kind of convention, consider these possible usages:

<!-- snippet: sample_durable_local_queues -->
<a id='snippet-sample_durable_local_queues'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.Policies.UseDurableLocalQueues();

        // or

        opts.LocalQueue("important").UseDurableInbox();

        // or conventionally, make the local queues for messages in a certain namespace
        // be durable
        opts.Policies.ConfigureConventionalLocalRouting().CustomizeQueues((type, queue) =>
        {
            if (type.IsInNamespace("MyApp.Commands.Durable"))
            {
                queue.UseDurableInbox();
            }
        });
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/PersistenceTests/Samples/DocumentationSamples.cs#L102-L123' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_durable_local_queues' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Message Identity <Badge type="tip" text="3.7" />

Wolverine was originally conceived for a world in which micro-services were all the rage for software architectures. 
The world changed on us though, as folks are now interested in pursuing [Modular Monolith architectures](/tutorials/modular-monolith) where
you may be trying to effectively jam what used to be separate micro-services into a single process. 

In the "classic" Wolverine configuration, incoming messages to the Wolverine transactional inboxes use the message id
of the incoming `Envelope` objects as the primary key in message stores. Which breaks down if you have something like this:

![Receiving Same Message 2 or More Times](/receive-message-twice.png)

In the diagram above, I'm trying to show what might happen (and it has happened) when the same Wolverine message is sent
through an external broker and delivered more than once to the same downstream Wolverine application. In the "classic"
mode, Wolverine will treat all but the first message as duplicate messages and reject them -- even though you mean
these messages to be handled separately by different message handlers in your modular monolith.

Not to worry, you can now opt into this setting to identify an incoming message by the combination of message id *and*
destination:

<!-- snippet: sample_configuring_message_identity_to_use_id_and_destination -->
<a id='snippet-sample_configuring_message_identity_to_use_id_and_destination'></a>
```cs
var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, "receiver2");
        
        // This setting changes the internal message storage identity
        opts.Durability.MessageIdentity = MessageIdentity.IdAndDestination;
    })
    .StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/SqlServerTests/Persistence/SqlServerMessageStore_with_IdAndDestination_Identity.cs#L34-L45' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_configuring_message_identity_to_use_id_and_destination' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

This might be an important setting for [modular monolith architectures](/tutorials/modular-monolith). 

## Stale Inbox and Outbox Thresholds

::: info
This is more a "defense in depth" feature than a common problem with the inbox/outbox mechanics. These
flags are "opt in" only because they require database schema changes.
:::

It should not ever be possible for messages to get "stuck" in the transactional inbox or outbox, but it's an 
imperfect world and occasionally there are hiccups that might lead to that situation. To that end, you have
these "opt in" settings to tell Wolverine to "bump" apparently stalled or stale messages back into play *just in case*:

<!-- snippet: sample_using_inbox_outbox_stale_time -->
<a id='snippet-sample_using_inbox_outbox_stale_time'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        // configure the actual message persistence...

        // This directs Wolverine to "bump" any messages marked
        // as being owned by a specific node but older than
        // these thresholds as  being open to any node pulling 
        // them in
        
        // TL;DR: make Wolverine go grab stale messages and make
        // sure they are processed or sent to the messaging brokers
        opts.Durability.InboxStaleTime = 5.Minutes();
        opts.Durability.OutboxStaleTime = 5.Minutes();
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/InboxOutboxSettings.cs#L11-L28' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_inbox_outbox_stale_time' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

::: info
These settings are opt-in; they have no default value unless you set them explicitly.
:::
