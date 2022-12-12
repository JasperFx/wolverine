## Message Operations at Runtime

The main entry point into Wolverine to initiate any message handling or publishing is the `IMessageBus` service
that is registered by Wolverine into your application's IoC container as a scoped service. Here's a brief sample
of the most common operations you'll use with `IMessageBus` and Wolverine itself:

There's also a second abstraction called `IMessageContext` that can be optionally consumed
within message handlers to add some extra operations and metadata for the
current message being processed in a handler:

```mermaid
classDiagram
    

class IMessageBus
class IMessageContext

IMessageContext ..> IMessageBus
IMessageContext --> Envelope : Current Message
```

Here's a quick sample usage of the most common operations you'll use with Wolverine:

<!-- snippet: sample_message_bus_basics -->
<a id='snippet-sample_message_bus_basics'></a>
```cs
public static async Task use_message_bus(IMessageBus bus)
{
    // Execute this command message right now! And wait until
    // it's completed or acknowledged
    await bus.InvokeAsync(new DebitAccount(1111, 100));
    
    // Execute this message right now, but wait for the declared response
    var status = await bus.InvokeAsync<AccountStatus>(new DebitAccount(1111, 250));
    
    // Send the message expecting there to be at least one subscriber to be executed later, but
    // don't wait around
    await bus.SendAsync(new DebitAccount(1111, 250));
    
    // Or instead, publish it to any interested subscribers, 
    // but don't worry about it if there are actually any subscribers
    // This is probably best for raising event messages
    await bus.PublishAsync(new DebitAccount(1111, 300));
    
    // Send a message to be sent or executed at a specific time
    await bus.ScheduleAsync(new DebitAccount(1111, 100), DateTimeOffset.UtcNow.AddDays(1));
    
    // Or do the same, but this time express the time as a delay
    await bus.ScheduleAsync(new DebitAccount(1111, 225.25L), 1.Days());
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/MessageBusBasics.cs#L8-L35' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_message_bus_basics' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

::: tip
The only practical difference between `Send` and `Publish` is that `Send` will assert that there is at least
one subscriber for the message and throw an exception if there is not.
:::

## Invoking Message Execution

To execute the message processing immediately and wait until it's finished, use this syntax:

<!-- snippet: sample_invoke_locally -->
<a id='snippet-sample_invoke_locally'></a>
```cs
public static async Task invoke_locally(IMessageBus bus)
{
    // Execute the message inline
    await bus.InvokeAsync(new Message1());
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/EnqueueSamples.cs#L11-L19' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_invoke_locally' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

HERE HERE HERE!!!!!!!!!!!!!!!!!!!

Note that this feature does utilize any registered [retry or retry with cooldown error handling rules](/guide/handlers/error-handling)
for potentially transient errors.

## Sending or Publishing Messages

[Publish/Subscribe](https://docs.microsoft.com/en-us/azure/architecture/patterns/publisher-subscriber) is a messaging pattern where the senders of messages do not need to specifically know what the specific subscribers are for a given message. In this case, some kind of middleware or infrastructure is responsible for either allowing subscribers to express interest in what messages they need to receive or apply routing rules to send the published messages to the right places. Wolverine's messaging support was largely built to support the publish/subscibe messaging patterm.

To send a message with Wolverine, use the `IMessageBus` interface or the bigger `IMessageContext` interface that
are registered in your application's IoC container. The sample below shows the most common usage:

<!-- snippet: sample_sending_message_with_servicebus -->
<a id='snippet-sample_sending_message_with_servicebus'></a>
```cs
public ValueTask SendMessage(IMessageContext bus)
{
    // In this case, we're sending an "InvoiceCreated"
    // message
    var @event = new InvoiceCreated
    {
        Time = DateTimeOffset.Now,
        Purchaser = "Guy Fieri",
        Amount = 112.34,
        Item = "Cookbook"
    };

    return bus.SendAsync(@event);
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/PublishingSamples.cs#L167-L184' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_sending_message_with_servicebus' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

That by itself will send the `InvoiceCreated` message to whatever subscribers are interested in
that message. The `SendAsync()` method will throw an exception if Wolverine doesn't know where to send the message. In other words,
there has to be a subscriber of some sort for that message.

On the other hand, the `PublishAsync()` method will send a message if there is a known subscriber and ignore the message if there is
no subscriber:

<!-- snippet: sample_publishing_message_with_servicebus -->
<a id='snippet-sample_publishing_message_with_servicebus'></a>
```cs
public ValueTask PublishMessage(IMessageContext bus)
{
    // In this case, we're sending an "InvoiceCreated"
    // message
    var @event = new InvoiceCreated
    {
        Time = DateTimeOffset.Now,
        Purchaser = "Guy Fieri",
        Amount = 112.34,
        Item = "Cookbook"
    };

    return bus.PublishAsync(@event);
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/PublishingSamples.cs#L187-L204' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_publishing_message_with_servicebus' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Scheduling Message Delivery or Execution


## Customizing Message Delivery

TODO -- more text here. NEW PAGE???

<!-- snippet: sample_SendMessagesWithDeliveryOptions -->
<a id='snippet-sample_sendmessageswithdeliveryoptions'></a>
```cs
public static async Task SendMessagesWithDeliveryOptions(IMessageBus bus)
{
    await bus.PublishAsync(new Message1(), new DeliveryOptions
        {
            AckRequested = true,
            ContentType = "text/xml", // you can do this, but I'm not sure why you'd want to override this
            DeliverBy = DateTimeOffset.Now.AddHours(1), // set a message expiration date
            DeliverWithin = 1.Hours(), // convenience method to set the deliver-by expiration date
            ScheduleDelay = 1.Hours(), // Send this in one hour, or...
            ScheduledTime = DateTimeOffset.Now.AddHours(1),
            ResponseType = typeof(Message2) // ask the receiver to send this message back to you if it can
        }
        // There's a chained fluent interface for adding header values too
        .WithHeader("tenant", "one"));
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/CustomizingMessageDelivery.cs#L9-L27' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_sendmessageswithdeliveryoptions' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->


