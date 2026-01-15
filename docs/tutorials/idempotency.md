# Idempotency in Messaging

::: tip
Wolverine's built in idempotency detection can only be used in conjunction with configured envelope storage.
:::

Wolverine should never be trying to publish or process the exact same message at the same endpoint more than once,
but it's an imperfect world and things can go weird in real life usage. Assuming that you have some sort of [message storage](/guide/durability/) enabled,
Wolverine can use its transactional inbox support to enforce message idempotency.

As usual, we're going to reach for the EIP book and what they described as an [Idempotent Receiver](https://www.enterpriseintegrationpatterns.com/patterns/messaging/IdempotentReceiver.html):

> Design a receiver to be an Idempotent Receiver--one that can safely receive the same message multiple times.

In practical terms, this means that Wolverine is able to use its incoming message storage to "know" whether it has
already processed an incoming message and discard any duplicate message with the same Wolverine message id that *somehow, some way*
manages to arrive twice from an external transport. The mechanism is a little bit different depending on the [Wolverine listening endpoint
mode](/guide/runtime.html#endpoint-types), it's always keying off the message id assigned by Wolverine.

Unless of course you're pursuing a [modular monolith architecture](/tutorials/modular-monolith) where you might be expecting the same identified
message to arrive and be processed separately in separate endpoints. In which case, this setting:

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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/SqlServerTests/Persistence/SqlServerMessageStore_with_IdAndDestination_Identity.cs#L28-L40' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_configuring_message_identity_to_use_id_and_destination' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Means that the uniqueness is the message id + the endpoint destination, which Wolverine stores as a `Uri` string in the 
various envelope storage databases. In all cases, Wolverine simply detects a [primary key](https://en.wikipedia.org/wiki/Primary_key) violation on the incoming envelope
storage to "know" that the message has already been handled. 

::: info
There are built in error policies in Wolverine (introduced in 5.3) to automatically [discard](/guide/handlers/error-handling.html#discarding-messages) any message that is determined to be a duplicate.
This is done through exception filters and matching based on exceptions thrown by the underlying message storage database, and there's
certainly a chance you *might* have to occasionally help Wolverine out with more exception filter rules to discard these messages
that can never be successfully processed.
:::

## In Durable Endpoints

::: info
Wolverine 5.2 and 5.3 both included improvements to the idempotency tracking and this documentation reflects those versions.
Before 5.2, Wolverine would try to mark the message as `Handled` after the full message was handled, but outside of any transaction
during the message handling. 
:::

Idempotency checking is turned on by default with `Durable` endpoints. When messages are received at a `Durable` endpoint, 
this is the sequence of steps:

1. The Wolverine listener creates the Wolverine `Envelope` for the incoming message
2. The Wolverine listener will try to insert the new incoming `Envelope` into the transactional inbox storage
3. If the `IMessageStore` for the system throws a `DuplicateIncomingEnvelopeException` on that operation, that's a duplicate, so Wolverine logs that
   and discards that message by "ack-ing" the message broker (that's a little different based on the actual underlying message transport technology)
4. Assuming the message is correctly stored in the inbox storage, Wolverine "acks" the message with the broker and puts the message into the in memory channel
   for processing
5. With at least the Marten or EF Core transactional middleware support, Wolverine will try to update the storage for the current message
   with the status `Handled` as part of the message handling transaction
6. If the envelope was not previously marked as `Handled`, the Wolverine listener will try to mark the stored message as `Handled` after the message
   completely succeeds

Also see the later section on message retention.

## Buffered or Inline Endpoints <Badge type="tip" text="5.3" />

::: tip
The idempotency checking is only possible within message handlers that have the transactional middleware applied.
:::

::: info
For `Buffered` or `Inline` endpoints, Wolverine is **only** storing metadata about the message and not the actual message body
or `Envelope`. It's just enough information to feed the idempotency checks and to satisfy expected database data constraints.
:::

::: warning
As of 5.4.1, **every** usage of explicit idempotency outside of durable listeners will
use `Eager` checking regardless of the configuration. The `Optimistic` mode has thus far
proven to be too buggy to be useful.
:::

Idempotency checking within message handlers executing within `Buffered` or more likely `Inline` listeners will require
you to "opt in." First though, the idempotency check in this case can be done in one of two modes:

1. `Eager` -- just means that Wolverine will apply some middleware around the handler such that it will make an early database call to try to insert a skeleton placeholder in the transactional inbox storage
2. `Optmistic` -- Wolverine will try to insert the skeleton message information as part of the message handling transaction to try to avoid extra database round trips

To be honest, the EF Core integration will always use the `Eager` approach no matter what. Marten supports both modes, and the `Optimistic`
approach may be valuable if all the activity of your message handler is in changes to that same database so everything can still be 
rolled back by the idempotency check failing. 

For another example, if your message handler involves a web service call to an external system or really any kind of action
that potentially makes state changes outside of the current transaction, you have to use the `Eager` mode.

With all of that being said, you can either opt into the idempotency checks one at a time with an overload of the `[Transactional]`
attribute like this:

<!-- snippet: sample_using_explicit_idempotency_on_single_handler -->
<a id='snippet-sample_using_explicit_idempotency_on_single_handler'></a>
```cs
[Transactional(IdempotencyStyle.Eager)]
public static void Handle(DoSomething msg)
{
    
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/Configuration/configuring_idempotency_style.cs#L106-L114' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_explicit_idempotency_on_single_handler' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Or you can use an overload of the auto apply transactions policy:

<!-- snippet: sample_setting_default_idempotency_check_level -->
<a id='snippet-sample_setting_default_idempotency_check_level'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.Policies.AutoApplyTransactions(IdempotencyStyle.Eager);
    })
    .StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/Configuration/configuring_idempotency_style.cs#L41-L50' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_setting_default_idempotency_check_level' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

::: tip
The idempotency check and the process of marking an incoming envelope are themselves "idempotent" within Wolverine
to avoid Wolverine from making unnecessary database calls. ~~~~
:::

## Idempotency on Non Transactional Handlers

Every usage you've seen so far has featured utilizing Wolverine's transactional middleware support on handlers that
use [EF Core](/guide/durability/efcore/transactional-middleware) or [Marten](/guide/durability/marten/transactional-middleware).

But of course, you may have message handlers that don't need to touch your underlying storage at all. For example, a message
handler might do nothing but call an external web service. You may want to make this message handler be idempotent to protect
against duplicated calls to that web service. You're in luck, because Wolverine exposes this policy to do exactly that:

snippet: sample_using_AutoApplyIdempotencyOnNonTransactionalHandlers

Specifically, see the call to `WolverineOptions.Policies.AutoApplyIdempotencyOnNonTransactionalHandlers()` above. What that
is doing is:

1. Inserting a call to assert that the current message doesn't already exist in your applications default envelope storage by
   the Wolverine message id. If the message is already marked as `Handled` in the inbox, Wolverine will reject and discard the current
   message processing
2. Assuming the message is all new, Wolverine will try to persist the `Handled` state in the default inbox storage. In the case
   of failures to the database storage (stuff happens), Wolverine will attempt to retry out of band, but allow the message processing
   to go through otherwise without triggering error policies so the message is not retried

::: tip
While we're talking about call outs to external web services, the Wolverine team recommends isolating the call to that web
service in its own handler with isolated error handling and maybe even a circuit breaker for outages of that service. Or at
least making that your default practice.
:::

## Handled Message Retention

The way that the idempotency checks work is to keep track of messages that have already been processed in the persisted
transactional inbox storage. But of course, you don't want that storage to grow forever and choke off the performance of your
system, so Wolverine has a background process to delete messages marked as `Handled` older than a configured threshold
with the setting shown below:

<!-- snippet: sample_configuring_KeepAfterMessageHandling -->
<a id='snippet-sample_configuring_KeepAfterMessageHandling'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        // The default is 5 minutes, but if you want to keep
        // messages around longer (or shorter) in case of duplicates,
        // this is how you do it
        opts.Durability.KeepAfterMessageHandling = 10.Minutes();
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/PersistenceTests/Samples/DocumentationSamples.cs#L195-L206' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_configuring_KeepAfterMessageHandling' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The default is to keep messages for at least 5 minutes. 


