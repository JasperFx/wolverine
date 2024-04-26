# Basics

::: warning
Heads up, at *this moment*, Wolverine requires the usage of the [Lamar IoC library](https://jasperfx.github.io/lamar) and will
quietly replace your application's service provider with Lamar. This will hopefully change in Wolverine 3.0, but the limitation
will likely be Lamar or the built in DI container.
:::

![Wolverine Messaging Architecture](/messages.jpeg)

One way or another, Wolverine is all about messages within your system or between systems. Staying inside a single Wolverine system,
a message is typically just a .NET class (or struct, but reserve that for strictly local usage) or C# record. A message generally
represents either a "command" that should trigger an operation or an "event" that just lets another part of your system know that 
something happened. Here's a couple simple samples:

<!-- snippet: sample_DebutAccount_command -->
<a id='snippet-sample_debutaccount_command'></a>
```cs
// A "command" message
public record DebitAccount(long AccountId, decimal Amount);

// An "event" message
public record AccountOverdrawn(long AccountId);
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/MessageBusBasics.cs#L69-L77' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_debutaccount_command' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The next concept in Wolverine is a message handler, which is just a method that "knows" how to process an incoming message. Here's an extremely
simple example:

<!-- snippet: sample_DebitAccountHandler -->
<a id='snippet-sample_debitaccounthandler'></a>
```cs
public static class DebitAccountHandler
{
    public static void Handle(DebitAccount account)
    {
        Console.WriteLine($"I'm supposed to debit {account.Amount} from account {account.AccountId}");
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/MessageBusBasics.cs#L57-L67' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_debitaccounthandler' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Wolverine can act as a completely local mediator tool that allows your code to invoke the handler for a message at any time without having
to know anything about exactly how that message is processed with this usage:

<!-- snippet: sample_invoke_debit_account -->
<a id='snippet-sample_invoke_debit_account'></a>
```cs
public async Task invoke_debit_account(IMessageBus bus)
{
    // Debit $250 from the account #2222
    await bus.InvokeAsync(new DebitAccount(2222, 250));
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/MessageBusBasics.cs#L37-L45' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_invoke_debit_account' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

There's certainly some value in Wolverine just being a command bus running inside of a single process, Wolverine also allows you to both publish and process messages received through external infrastructure like [Rabbit MQ](https://www.rabbitmq.com/)
or [Pulsar](https://pulsar.apache.org/).

To put this into perspective, here's how a Wolverine application could be connected to the outside world:

![Wolverine Messaging Architecture](/WolverineMessaging.png)

:::tip
The diagram above should just say "Message Handler" as Wolverine makes no structural differentiation between commands or events, but Jeremy is being too lazy to fix the diagram.
:::


## Terminology

* *Message* -- Typically just a .NET class or C# record that can be easily serialized. See [messages and serialization](/guide/messages) for more information
* *Envelope* -- Wolverine's [Envelope Wrapper](https://www.enterpriseintegrationpatterns.com/patterns/messaging/EnvelopeWrapper.html) model that wraps the raw messages with metadata 
* *Message Handler* -- A method or function that "knows" how to process an incoming message. See [Message Handlers](/guide/handlers/) for more information
* *Transport* -- This refers to the support within Wolverine for external messaging infrastructure tools like [Rabbit MQ](/guide/messaging/transports/rabbitmq/), [Amazon SQS](/guide/messaging/transports/sqs/), [Azure Service Bus](/guide/messaging/transports/azure-service-bus/), or Wolverine's built in [TCP transport](/guide/messaging/transports/tcp)
* *Endpoint* -- The configuration for a Wolverine connection to some sort of external resource like a Rabbit MQ exchange or an Amazon SQS queue. The [Async API](https://www.asyncapi.com/) specification refers to this as a *channel*, and Wolverine may very well change its nomenclature in the future to be consistent with Async API. 
* *Sending Agent* -- You won't use this directly in your own code, but Wolverine's internal adapters to publish outgoing messages to transport endpoints
* *Listening Agent* -- Again, an internal detail of Wolverine that receives messages from external transport endpoints, and mediates between the transports and executing the message handlers
* *Message Store* -- Database storage for Wolverine's [inbox/outbox persistent messaging](/guide/durability/)
* *Durability Agent* -- An internal subsystem in Wolverine that runs in a background service to interact with the message store for Wolverine's [transactional inbox/outbox](https://microservices.io/patterns/data/transactional-outbox.html) functionality

For more context, see 
