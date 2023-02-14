# Message Handler Discovery

Wolverine has built in mechanisms for automatically finding message handler methods in your application
or the ability to explicitly add handler types. The conventional discovery can
be disabled or customized as well.

## Default Conventional Discovery

Wolverine uses Lamar's type scanning (based on [StructureMap 4.0's type scanning support](http://structuremap.github.io/registration/auto-registration-and-conventions/)) to find
handler classes and candidate methods from known assemblies based on naming conventions.

By default, Wolverine is looking for public classes in the main application assembly with names matching these rules:

* Type name ends with "Handler"
* Type name ends with "Consumer"

From the types, Wolverine looks for any public instance method that either accepts a single parameter that is assumed to be the message type, or **one** parameter with one of these names: *message*, *input*, *command*, or *@event*. In addition,
Wolverine will also pick the first parameter as the input type regardless of parameter name if it is concrete, not a "simple" type like a string, date, or number, and not a "Settings" type.

To make that concrete, here are some valid handler method signatures:

<!-- snippet: sample_ValidMessageHandlers -->
<a id='snippet-sample_validmessagehandlers'></a>
```cs
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/HandlerExamples.cs#L10-L68' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_validmessagehandlers' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The valid method names are:

1. Handle / HandleAsync
2. Handles / HandlesAsync
3. Consume / ConsumeAsync
4. Consumes / ConsumesAsync

And also specific to sagas: 

1. Start / StartAsync
2. Starts / StartAsync
3. Orchestrate / OrchestrateAsync
4. Orchestrates / OrchestratesAsync
5. StartOrHandle / StartOrHandleAsync
6. StartsOrHandles / StartsOrHandlesAsync
7. NotFound / NotFoundAsync

See [Stateful Sagas](/guide/durability/sagas) for more information. 

## Disabling Conventional Discovery

You can completely turn off any automatic discovery of message handlers through type scanning by
using this syntax in your `WolverineOptions`:

<!-- snippet: sample_ExplicitHandlerDiscovery -->
<a id='snippet-sample_explicithandlerdiscovery'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        // No automatic discovery of handlers
        opts.DisableConventionalDiscovery();
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/HandlerExamples.cs#L213-L222' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_explicithandlerdiscovery' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Explicitly Ignoring Methods

You can force Wolverine to disregard a candidate message handler action at either the class or method
level by using the `[WolverineIgnore]` attribute like this:

<!-- snippet: sample_WolverineIgnoreAttribute -->
<a id='snippet-sample_wolverineignoreattribute'></a>
```cs
public class NetflixHandler : IMovieSink
{
    public void Listen(MovieAdded added)
    {
    }

    public void Handles(IMovieEvent @event)
    {
    }

    public void Handles(MovieEvent @event)
    {
    }

    public void Consume(MovieAdded added)
    {
    }

    // Only this method will be ignored as
    // a handler method
    [WolverineIgnore]
    public void Handles(MovieAdded added)
    {
    }

    public void Handle(MovieAdded message, IMessageContext context)
    {
    }

    public static Task Handle(MovieRemoved removed)
    {
        return Task.CompletedTask;
    }
}

// All methods on this class will be ignored
// as handler methods even though the class
// name matches the discovery naming conventions
[WolverineIgnore]
public class BlockbusterHandler
{
    public void Handle(MovieAdded added)
    {
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/Configuration/find_handlers_with_the_default_handler_discovery.cs#L161-L209' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_wolverineignoreattribute' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->


## Customizing Conventional Discovery

::: warning
Do note that handler finding conventions are additive, meaning that adding additional criteria does
not disable the built in handler discovery
:::

The easiest way to use the Wolverine messaging functionality is to just code against the default conventions. However, if you wish to deviate
from those naming conventions you can either supplement the handler discovery or replace it completely with your own conventions.

At a minimum, you can disable the built in discovery, add additional type filtering criteria, or register specific handler classes with the code below:

<!-- snippet: sample_CustomHandlerApp -->
<a id='snippet-sample_customhandlerapp'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.Policies.Discovery(x =>
        {
            // Turn off the default handler conventions
            // altogether
            x.DisableConventionalDiscovery();

            // Include candidate actions by a user supplied
            // type filter
            x.IncludeTypes(t => t.IsInNamespace("MyApp.Handlers"));

            // Include candidate classes by suffix
            x.IncludeClassesSuffixedWith("Listener");

            // Include a specific handler class with a generic argument
            x.IncludeType<SimpleHandler>();
        });
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/HandlerDiscovery.cs#L129-L152' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_customhandlerapp' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->


## Subclass or Interface Handlers

Wolverine will allow you to use handler methods that work against interfaces or abstract types to apply or reuse
generic functionality across messages. Let's say that some subset of your messages implement some kind of
`IMessage` interface like this one and an implementation of it below:

<!-- snippet: sample_Handlers_IMessage -->
<a id='snippet-sample_handlers_imessage'></a>
```cs
public interface IMessage
{
}

public class MessageOne : IMessage
{
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/HandlerDiscovery.cs#L52-L62' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_handlers_imessage' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

You can handle the `MessageOne` specifically with a handler action like this:

<!-- snippet: sample_Handlers_SpecificMessageHandler -->
<a id='snippet-sample_handlers_specificmessagehandler'></a>
```cs
public class SpecificMessageHandler
{
    public void Consume(MessageOne message)
    {
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/HandlerDiscovery.cs#L76-L85' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_handlers_specificmessagehandler' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

You can also create a handler for `IMessage` like this one:

<!-- snippet: sample_Handlers_GenericMessageHandler -->
<a id='snippet-sample_handlers_genericmessagehandler'></a>
```cs
public class GenericMessageHandler
{
    public void Consume(IMessage messagem, Envelope envelope)
    {
        Console.WriteLine($"Got a message from {envelope.Source}");
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/HandlerDiscovery.cs#L64-L74' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_handlers_genericmessagehandler' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

When Wolverine handles the `MessageOne` message, it first calls all the specific handlers for that message type,
then will call any handlers that handle a more generic message type (interface or abstract class most likely) where
the specific type can be cast to the generic type.
