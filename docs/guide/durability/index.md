# Durable Messaging

::: info
A major goal of Wolverine 4.0 is to bring the EF Core integration capabilities (including multi-tenancy support) up to match the current integration
with Marten, add event sourcing support for SQL Server, and at least envelope storage integration with CosmosDb.
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

<!-- snippet: sample_DebitAccountHandler_that_uses_IMessageContext -->
<a id='snippet-sample_DebitAccountHandler_that_uses_IMessageContext'></a>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/Middleware/AppWithMiddleware/Account.cs#L126-L161' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_DebitAccountHandler_that_uses_IMessageContext' title='Start of snippet'>anchor</a></sup>
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
storage and processed when the system is restarted. Wolverine does this through its [DurabilityAgent](https://github.com/JasperFx/wolverine/blob/main/src/Wolverine/Persistence/Durability/DurabilityAgent.cs) that will run within your application through Wolverine's
[IHostedService](https://docs.microsoft.com/en-us/aspnet/core/fundamentals/host/hosted-services?view=aspnetcore-6.0&tabs=visual-studio) runtime that is automatically registered in your system through the `UseWolverine()` extension method.

::: tip
At the moment, Wolverine only supports Postgresql, Sql Server, and RavenDb as the underlying database and either [Marten](/guide/durability/marten) or
[Entity Framework Core](/guide/durability/efcore) as the application persistence framework.
:::

There are four things you need to enable for the transactional outbox (and inbox for incoming messages):

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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/PersistenceTests/Samples/DocumentationSamples.cs#L68-L80' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_make_specific_subscribers_be_durable' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/PersistenceTests/Samples/DocumentationSamples.cs#L53-L63' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_make_all_subscribers_be_durable' title='Start of snippet'>anchor</a></sup>
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
an imperfect world. If you are using one of the relational backed message stores for Wolverine (SQL Server or PostgreSQL at this point),
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/PersistenceTests/Samples/DocumentationSamples.cs#L281-L299' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_configuring_outbox_stale_timeout' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/PersistenceTests/Samples/DocumentationSamples.cs#L85-L101' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_configuring_durable_inbox' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/PersistenceTests/Samples/DocumentationSamples.cs#L106-L128' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_durable_local_queues' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/SqlServerTests/Persistence/SqlServerMessageStore_with_IdAndDestination_Identity.cs#L34-L46' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_configuring_message_identity_to_use_id_and_destination' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/InboxOutboxSettings.cs#L11-L29' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_inbox_outbox_stale_time' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

::: info
These settings will be defaults in Wolverine 6.0.
:::
