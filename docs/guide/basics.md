# Basics

Before going into any kind of detail about how to use Wolverine, let's talk about some terminology:

* *Message* -- Typically just a .NET class or C# record that can be easily serialized. See [messages and serialization](/guide/messages) for more information
* *Message Handler* -- A method or function that "knows" how to process an incoming message. See [Message Handlers](/guide/handlers/) for more information
* *Transport* -- This refers to the support within Wolverine for external messaging infrastructure tools like Rabbit MQ or Pulsar
* *Endpoint* -- A Wolverine connection to some sort of external resource like a Rabbit MQ exchange or an Amazon SQS queue. The [Async API](https://www.asyncapi.com/) specification refers to this as a *channel*, and Wolverine may very well change its nomenclature in the future to be consistent with Async API
* *Sending Agent* -- You won't use this directly in your own code, but Wolverine's internal adapters to publish outgoing messages to transport endpoints
* *Listening Agent* -- Again, an internal detail of Wolverine that receives messages from external transport endpoints, and mediates between the transports and executing the message handlers
* *Message Store* -- Database storage for Wolverine's [inbox/outbox persistent messaging](/guide/persistence/)
* *Durability Agent* -- An internal subsystem in Wolverine that runs in a background service to interact with the message store for Wolverine's [transactional inbox/outbox](https://microservices.io/patterns/data/transactional-outbox.html) functionality

## IMessageBus

The main entry point into Wolverine to initiate any message handling or publishing is the `IMessageBus` service
that is registered by Wolverine into your application's IoC container as a scoped service. Here's a brief sample
of the most common operations you'll use with `IMessageBus` and Wolverine itself:

<!-- snippet: sample_message_bus_basics -->
<a id='snippet-sample_message_bus_basics'></a>
```cs
public static async Task use_message_bus(IMessageBus bus)
{
    // Execute this command message right now! And wait until
    // it's completed or acknowledged
    await bus.InvokeAsync(new DebitAccount(100));
    
    // Execute this message right now, but wait for the declared response
    var status = await bus.InvokeAsync<AccountStatus>(new DebitAccount(250));
    
    // Send the message expecting there to be at least one subscriber to be executed later, but
    // don't wait around
    await bus.SendAsync(new DebitAccount(250));
    
    // Or instead, publish it to any interested subscribers, 
    // but don't worry about it if there are actually any subscribers
    // This is probably best for raising event messages
    await bus.PublishAsync(new DebitAccount(300));
    
    // Send a message to be sent or executed at a specific time
    await bus.ScheduleAsync(new DebitAccount(100), DateTimeOffset.UtcNow.AddDays(1));
    
    // Or do the same, but this time express the time as a delay
    await bus.ScheduleAsync(new DebitAccount(225.25L), 1.Days());
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/MessageBusBasics.cs#L8-L35' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_message_bus_basics' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

::: tip
The only practical difference between `Send` and `Publish` is that `Send` will assert that there is at least
one subscriber for the message and throw an exception if there is not.
:::

For more information about the particulars, see:

TODO -- add links here