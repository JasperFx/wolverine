# Durable Inbox and Outbox Messaging

One of Wolverine's most important features is durable message persistence using your application's database for reliable "[store and forward](https://en.wikipedia.org/wiki/Store_and_forward)" queueing with all possible Wolverine transport options, including the lightweight <[linkto:documentation/integration/transports/tcp]> and external transports like <[linkto:documentation/integration/transports/rabbitmq]> or and <[linkto:documentation/integration/transports/azureservicebus]>.

It's a chaotic world out when high volume systems need to interact with other systems. Your system may fail, other systems may be down,
there's network hiccups, occasional failures -- and you still need your systems to get to a consistent state without messages just
getting lost en route.

To that end, Wolverine relies on message persistence within your application database as it's implementation of the [Transactional Outbox](https://microservices.io/patterns/data/transactional-outbox.html) pattern. Using the "outbox" pattern is a way to avoid the need for problematic
and slow [distributed transactions](https://en.wikipedia.org/wiki/Distributed_transaction) while still maintaining eventual consistency between database changes and the outgoing messages that are part of the logical transaction. Wolverine implementation of the outbox pattern
also includes a separate *message relay* process that will send the persisted outgoing messages in background processes (it's done by marshalling the outgoing message envelopes through [TPL Dataflow](https://docs.microsoft.com/en-us/dotnet/standard/parallel-programming/dataflow-task-parallel-library) queues if you're curious.)

If any node of a Wolverine system that uses durable messaging goes down before all the messages are processed, the persisted messages will be loaded from
storage and processed when the system is restarted. Wolverine does this through its [DurabilityAgent](https://github.com/JasperFx/wolverine/blob/master/src/Wolverine/Persistence/Durability/DurabilityAgent.cs) that will run within your application through Wolverine's
[IHostedService](https://docs.microsoft.com/en-us/aspnet/core/fundamentals/host/hosted-services?view=aspnetcore-6.0&tabs=visual-studio) runtime that is automatically registered in your system through the `UseWolverine()` extension method.

::: tip
At the moment, Wolverine only supports Postgresql or Sql Server as the underlying database and either [Marten](/marten) or
[Entity Framework Core](/efcore) as the application persistence framework.
:::

## Using the Outbox

TODO -- show enabling for one endpoint at a time
TODO -- show configuring by convention

## Using the Inbox

TODO -- show enabling for one endpoint at a time
TODO -- show configuring by convention


## Database Schema Objects

TODO -- link to dead letter queue actions

Regardless of database engine, Wolverine will add these database tables:

1. `wolverine_incoming_envelopes` - stores incoming and scheduled envelopes until they are successfully processed
1. `wolverine_outgoing_envelopes` - stores outgoing envelopes until they are successfully sent through the transports
1. `wolverine_dead_letters` - stores "dead letter" envelopes that could not be processed when using the local transport or any other kind of transport that does not natively support dead letter queues.

In the case of Sql Server, you'll see extra functions for the durability agent:

1. `uspDeleteIncomingEnvelopes`
1. `uspDeleteOutgoingEnvelopes`
1. `uspDiscardAndReassignOutgoing`
1. `uspMarkIncomingOwnership`
1. `uspMarkOutgoingOwnership`

TODO -- show using the Weasel stuff to set up the database on the fly on startup
TODO -- show the command line usage to dump the SQL or set up the database

## Using Sql Server


## Using Postgresql
