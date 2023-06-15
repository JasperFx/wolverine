# Message Handlers

::: tip
Wolverine's guiding philosophy is to remove code ceremony from a developer's day to day coding, but that comes
at the cost of using conventions that some developers will decry as "too much magic." If you actually prefer having
explicit interfaces or base classes or required attributes to direct your code, Wolverine will let you do that too, so don't go 
anywhere!
:::

Since the whole purpose of Wolverine is to connect incoming messages to handling code, most of your time as a user of Wolverine is going to be spent
writing and testing Wolverine message handlers. Let's just jump right into the simplest possible message handler implementation:

<!-- snippet: sample_simplest_possible_handler -->
<a id='snippet-sample_simplest_possible_handler'></a>
```cs
public class MyMessageHandler
{
    public void Handle(MyMessage message)
    {
        Console.WriteLine("I got a message!");
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/HandlerExamples.cs#L77-L87' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_simplest_possible_handler' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

If you've used other messaging, command execution, or so called "mediator" tool in .NET, you'll surely notice the absence of any kind of
required `IHandler<T>` type interface that frameworks typically require in order to make your custom code executable by the framework. Instead,
Wolverine intelligently wraps dynamic code around *your* code based on naming conventions so as to
allow *you* to just write plain old .NET code without any framework specific artifacts in your way.

Back to the handler code, at the point which you pass a new message into Wolverine like so:

<!-- snippet: sample_publish_MyMessage -->
<a id='snippet-sample_publish_mymessage'></a>
```cs
public static async Task publish_command(IMessageBus bus)
{
    await bus.PublishAsync(new MyMessage());
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/HandlerExamples.cs#L91-L98' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_publish_mymessage' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Between the call to `IMessageBus.PublishAsync()` and `MyMessageHandler.Handle(MyMessage)` there's a couple things
going on:

1. Wolverine's built in, [automatic handler discovery](/guide/handlers/discovery) has to find the candidate message handler methods
   and correlate them by message type
2. Wolverine's [runtime message processing](/guide/runtime) builds some connective code at runtime to relay the
   messages passed into `IMessageBus` to the right message handler methods

Before diving into the exact rules for message handlers, here are some valid handler methods:

<!-- snippet: sample_ValidMessageHandlers -->
<a id='snippet-sample_validmessagehandlers'></a>
```cs
[WolverineHandler]
public class ValidMessageHandlers
{
    // There's only one argument, so we'll assume that
    // argument is the message
    public void Handle(Message1 something)
    {
    }

    // The parameter named "message" is assumed to be the message type
    public Task ConsumeAsync(Message1 message, IDocumentSession session)
    {
        return session.SaveChangesAsync();
    }

    // In this usage, we're "cascading" a new message of type
    // Message2
    public Task<Message2> HandleAsync(Message1 message, IDocumentSession session)
    {
        return Task.FromResult(new Message2());
    }

    // In this usage we're "cascading" 0 to many additional
    // messages from the return value
    public IEnumerable<object> Handle(Message3 message)
    {
        yield return new Message1();
        yield return new Message2();
    }

    // It's perfectly valid to have multiple handler methods
    // for a given message type. Each will be called in sequence
    // they were discovered
    public void Consume(Message1 input, IEmailService emails)
    {
    }

    // It's also legal to handle a message by an abstract
    // base class or an implemented interface.
    public void Consume(IEvent @event)
    {
    }

    // You can inject additional services directly into the handler
    // method
    public ValueTask ConsumeAsync(Message3 weirdName, IEmailService service)
    {
        return ValueTask.CompletedTask;
    }

    public interface IEvent
    {
        string CustomerId { get; }
        Guid Id { get; }
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/HandlerExamples.cs#L10-L69' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_validmessagehandlers' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Rules for Message Handlers

::: info
The naming conventions in Wolverine are descended from a much earlier tool and the exact origins of
the particular names are lost in the mist of time
:::

* Message handlers must be public types with a public constructor. Sorry folks, but the code generation strategy
  that Wolverine uses requires this.
* Likewise, the handler methods must also be public
* Yet again, the message type must be public
* The first argument of the handler method must be the message type
* It's legal to connect multiple handler methods to a single message type. Whether that's a good idea or not
  is up to you and your use case
* Handler methods can be either instance methods or static methods
* It's legal to accept either an interface or abstract class as a message type, but read the documentation on that below first

For naming conventions:

* Handler type names should be suffixed with either `Handler` or `Consumer`
* Handler method names should be either `Handle()` or `Consume()`

Also see [stateful sagas](/guide/durability/sagas) as they have some additional rules.

See [return values](./return-values) for much more information about what types can be returned from a handler method and how Wolverine
would use those values.

## Message Handler Parameters

::: info
If you're thinking to yourself, hmm, the method injection seems a lot like ASP.NET Core Minimal APIs,
Wolverine has been baking an embarrassingly long time and had that implemented years earlier. Just saying.
:::

The first argument always has to be the message type, but after that, you can accept:

* Additional services from your application's Lamar IoC container
* `Envelope` from Wolverine to interrogate metadata about the current message
* `IMessageContext` or `IMessageBus` from Wolverine scoped to the current message being handled
* `CancellationToken` for the current message execution to check for timeouts or system shut down
* `DateTime now` or `DateTimeOffset now` for the current time. Don't laugh, I like doing this for testability's sake.

Some add ons or middleware add other possibilities as well.

## Handler Lifecycle & Service Dependencies

Handler methods can be instance methods on handler classes if it's desirable to scope the handler object to the message:

<!-- snippet: sample_ExampleHandlerByInstance -->
<a id='snippet-sample_examplehandlerbyinstance'></a>
```cs
public class ExampleHandler
{
    public void Handle(Message1 message)
    {
        // Do work synchronously
    }

    public Task Handle(Message2 message)
    {
        // Do work asynchronously
        return Task.CompletedTask;
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/HandlerExamples.cs#L104-L120' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_examplehandlerbyinstance' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

When using instance methods, the containing handler type will be scoped to a single message and be
disposed afterward. In the case of instance methods, it's perfectly legal to use constructor injection
to resolve IoC registered dependencies as shown below:

<!-- snippet: sample_HandlerBuiltByConstructorInjection -->
<a id='snippet-sample_handlerbuiltbyconstructorinjection'></a>
```cs
public class ServiceUsingHandler
{
    private readonly IDocumentSession _session;

    public ServiceUsingHandler(IDocumentSession session)
    {
        _session = session;
    }

    public Task Handle(InvoiceCreated created)
    {
        var invoice = new Invoice { Id = created.InvoiceId };
        _session.Store(invoice);

        return _session.SaveChangesAsync();
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/HandlerExamples.cs#L148-L168' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_handlerbuiltbyconstructorinjection' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

::: tip
Using a static method as your message handler can be a small performance
improvement by avoiding the need to create and garbage collect new objects at runtime.
:::

As an alternative, you can also use static methods as message handlers:

<!-- snippet: sample_ExampleHandlerByStaticMethods -->
<a id='snippet-sample_examplehandlerbystaticmethods'></a>
```cs
public static class ExampleHandler
{
    public static void Handle(Message1 message)
    {
        // Do work synchronously
    }

    public static Task Handle(Message2 message)
    {
        // Do work asynchronously
        return Task.CompletedTask;
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/HandlerExamples.cs#L125-L141' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_examplehandlerbystaticmethods' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The handler classes can be static classes as well. This technique gets much more useful when combined with Wolverine's
support for method injection in a following section.


## Method Injection

Similar to ASP.NET Core, Wolverine supports the concept of [method injection](https://www.martinfowler.com/articles/injection.html) in handler methods where you can just accept additional
arguments that will be passed into your method by Wolverine when a new message is being handled.

Below is an example action method that takes in a dependency on an `IDocumentSession` from [Marten](https://jasperfx.github.io/marten/):

<!-- snippet: sample_HandlerUsingMethodInjection -->
<a id='snippet-sample_handlerusingmethodinjection'></a>
```cs
public static class MethodInjectionHandler
{
    public static Task Handle(InvoiceCreated message, IDocumentSession session)
    {
        var invoice = new Invoice { Id = message.InvoiceId };
        session.Store(invoice);

        return session.SaveChangesAsync();
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/HandlerExamples.cs#L175-L188' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_handlerusingmethodinjection' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

So, what can be injected as an argument to your message handler?

1. Any service that is registered in your application's IoC container
1. `Envelope`
1. The current time in UTC if you have a parameter like `DateTime now` or `DateTimeOffset now`
1. Services or variables that match a registered code generation strategy. 

## Cascading Messages from Actions

See [Cascading Messages](/guide/handlers/cascading) for more details on this feature.


## "Compound Handlers"

It's frequently advantageous to split message handling for a single message up into methods that load any necessary data and the business logic that
transforms the current state or decides to take other actions. Wolverine allows you to use the [conventional middleware naming conventions](/guide/handlers/middleware.html#conventional-middleware) on each handler
to do exactly this. 

Consider the case of a message handler that is used to initiate the shipment of an order. That handler will ultimately need to load 
data for both the order itself and the customer information in order to figure out exactly what to ship out, how to ship it (overnight air? 2 day ground delivery?), and where.

Using Wolverine's compound handler feature, that might look like this: 

<!-- snippet: sample_ShipOrderHandler -->
<a id='snippet-sample_shiporderhandler'></a>
```cs
public static class ShipOrderHandler
{
    // This would be called first
    public static async Task<(Order, Customer)> LoadAsync(ShipOrder command, IDocumentSession session)
    {
        var order = await session.LoadAsync<Order>(command.OrderId);
        if (order == null)
        {
            throw new MissingOrderException(command.OrderId);
        }

        var customer = await session.LoadAsync<Customer>(command.CustomerId);

        return (order, customer);
    }

    // By making this method completely synchronous and having it just receive the
    // data it needs to make determinations of what to do next, Wolverine makes this
    // business logic easy to unit test
    public static IEnumerable<object> Handle(ShipOrder command, Order order, Customer customer)
    {
        // use the command data, plus the related Order & Customer data to 
        // "decide" what action to take next

        yield return new MailOvernight(order.Id);
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/CompoundHandlerSamples.cs#L31-L61' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_shiporderhandler' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->


## Using the Message Envelope

To access the `Envelope` for the current message being handled in your message handler, just accept `Envelope` as a method
argument like this:

<!-- snippet: sample_HandlerUsingEnvelope -->
<a id='snippet-sample_handlerusingenvelope'></a>
```cs
public class EnvelopeUsingHandler
{
    public void Handle(InvoiceCreated message, Envelope envelope)
    {
        var howOldIsThisMessage =
            DateTimeOffset.Now.Subtract(envelope.SentAt);
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/HandlerExamples.cs#L191-L202' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_handlerusingenvelope' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->


## Using the Current IMessageContext

If you want to access or use the current `IMessageContext` for the message being handled to send response messages
or maybe to enqueue local commands within the current outbox scope, just take in `IMessageContext` as a method argument
like in this example:

<!-- snippet: sample_PingHandler -->
<a id='snippet-sample_pinghandler'></a>
```cs
using Messages;
using Microsoft.Extensions.Logging;
using Wolverine;

namespace Ponger;

public class PingHandler
{
    public ValueTask Handle(Ping ping, ILogger<PingHandler> logger, IMessageContext context)
    {
        logger.LogInformation("Got Ping #{Number}", ping.Number);
        return context.RespondToSenderAsync(new Pong { Number = ping.Number });
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/PingPong/Ponger/PingHandler.cs#L1-L18' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_pinghandler' title='Start of snippet'>anchor</a></sup>
<a id='snippet-sample_pinghandler-1'></a>
```cs
public static class PingHandler
{
    // Simple message handler for the PingMessage message type
    public static ValueTask Handle(
        // The first argument is assumed to be the message type
        PingMessage message,

        // Wolverine supports method injection similar to ASP.Net Core MVC
        // In this case though, IMessageContext is scoped to the message
        // being handled
        IMessageContext context)
    {
        AnsiConsole.Write($"[blue]Got ping #{message.Number}[/]");

        var response = new PongMessage
        {
            Number = message.Number
        };

        // This usage will send the response message
        // back to the original sender. Wolverine uses message
        // headers to embed the reply address for exactly
        // this use case
        return context.RespondToSenderAsync(response);
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/PingPongWithRabbitMq/Ponger/PingHandler.cs#L6-L35' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_pinghandler-1' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

