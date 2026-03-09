# Message Handler Discovery

::: warning
The handler type scanning and discovery is done against an allow list of assemblies rather than
running through your entire application's dependency tree. Watch for this if handlers are missing.
:::

Wolverine has built in mechanisms for automatically finding message handler methods in your application
based on a set of naming conventions or using explicit interface or attribute markers. If you really wanted to, 
you could also explicitly add handler types programmatically.

## Troubleshooting Handler Discovery

It's an imperfect world and sometimes Wolverine isn't finding handler methods for some reason or another -- or
seems to be using types and methods you'd rather it didn't. Not to fear, there are some diagnostic tools
to help Wolverine explain what's going on.

Directly on `WolverineOptions` itself is a diagnostic method named `DescribeHandlerMatch` that will give
you a full textual report on why or why not Wolverine is identifying that type as a handler type, then if
it is found by Wolverine to be a handler type, 
also giving you a full report on each public method about why or why not Wolverine considers it to be a valid
handler method.

<!-- snippet: sample_describe_handler_match -->
<a id='snippet-sample_describe_handler_match'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        // Surely plenty of other configuration for Wolverine...

        // This *temporary* line of code will write out a full report about why or
        // why not Wolverine is finding this handler and its candidate handler messages
        Console.WriteLine(opts.DescribeHandlerMatch(typeof(MyMissingMessageHandler)));
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/HandlerDiscoverySamples.cs#L148-L160' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_describe_handler_match' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Even if the report itself isn't exactly clear to you, using this textual report in a Wolverine issue or
within the [Critter Stack Discord](https://discord.gg/wBkZGpe3) group will help the Wolverine team be able to assist you much quicker. 

## Assembly Discovery

::: tip
The handler discovery uses the type scanning functionality into `JasperFx.Core` library for type scanning that is shared with several other JasperFx projects. 
:::

The first issue is which assemblies will Wolverine look through to find candidate handlers? By default, Wolverine is looking through what
it calls the *application assembly*. When you call `IHostBuilder.UseWolverine()` to add Wolverine to an application, Wolverine looks up the call
stack to find where the call to that method came from, and uses that to determine the application assembly. If you're using an idiomatic
approach to bootstrap your application through `Program.Main(args)`, the application assembly is going to be the application's main assembly that holds the
`Program.Main()` entrypoint.

::: tip
We highly recommend you use [WebApplicationFactory](https://learn.microsoft.com/en-us/aspnet/core/test/integration-tests?view=aspnetcore-7.0) or [Alba](https://jasperfx.github.io/alba) (which uses `WebApplicationFactory` behind the covers)
to bootstrap your application in integration tests to avoid any problems around Wolverine's application assembly determination.
:::

In testing scenarios, if you're bootstrapping the application independently somehow of the application's "official" configuration, you may have to help
Wolverine out a little bit and explicitly tell it what the application assembly is:

<!-- snippet: sample_overriding_application_assembly -->
<a id='snippet-sample_overriding_application_assembly'></a>
```cs
using var host = Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        // Override the application assembly to help
        // Wolverine find its handlers
        // Should not be necessary in most cases
        opts.ApplicationAssembly = typeof(Program).Assembly;
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/BootstrappingSamples.cs#L10-L21' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_overriding_application_assembly' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

To pull in handlers from other assemblies, you can either decorate an assembly with this attribute:

<!-- snippet: sample_using_wolverine_module_attribute -->
<a id='snippet-sample_using_wolverine_module_attribute'></a>
```cs
using Wolverine.Attributes;

[assembly: WolverineModule]
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/OrderExtension/Handlers.cs#L1-L7' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_wolverine_module_attribute' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Or you can programmatically add additional assemblies to the handler discovery with this syntax:

<!-- snippet: sample_adding_extra_assemblies_to_type_discovery -->
<a id='snippet-sample_adding_extra_assemblies_to_type_discovery'></a>
```cs
using var host = Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        // Add as many other assemblies as you need
        opts.Discovery.IncludeAssembly(typeof(MessageFromOtherAssembly).Assembly);
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/BootstrappingSamples.cs#L26-L35' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_adding_extra_assemblies_to_type_discovery' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Handler Type Discovery

::: warning
Wolverine does not support any kind of open generic types for message handlers and has no intentions of ever doing so.
:::

By default, Wolverine is looking for public, concrete classes that follow any of these rules:

* Implements the `Wolverine.IWolverineHandler` interface
* Is decorated with the `[Wolverine.WolverineHandler]` attribute
* Type name ends with "Handler"
* Type name ends with "Consumer"

The original intention was to strictly use naming conventions to locate message handlers, but if you prefer a more explicit approach
for discovery, feel free to utilize the `IWolverineHandler` interface or `[WolverineHandler]` (you'll have to use the attribute approach for static classes).

From the types, by default, Wolverine looks for any public instance method that is:

1. Is named `Handle`, `Handles`, `Consume`, `Consumes` or one of the names from [Wolverine's saga support](/guide/durability/sagas)
2. Is decorated by the `[WolverineHandler]` attribute if you want to use a different, descriptive name

In all cases, Wolverine assumes that the first argument is the incoming message.

To make that concrete, here are some valid handler method signatures:

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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/HandlerExamples.cs#L10-L63' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_validmessagehandlers' title='Start of snippet'>anchor</a></sup>
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

::: warning
Note that disabling conventional discovery will *also* disable any customizations you may have made to the 
conventional handler discovery
:::

You can completely turn off any automatic discovery of message handlers through type scanning by
using this syntax in your `WolverineOptions`:

<!-- snippet: sample_ExplicitHandlerDiscovery -->
<a id='snippet-sample_explicithandlerdiscovery'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        // No automatic discovery of handlers
        opts.Discovery.DisableConventionalDiscovery();
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/HandlerExamples.cs#L227-L236' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_explicithandlerdiscovery' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Replacing the Handler Discovery Rules <Badge type="tip" text="3.10" />

You can completely replace Wolverine's handler type discovery by first disabling the conventional handler discovery,
then adding all new rules like this:

<!-- snippet: sample_replacing_handler_discovery_rules -->
<a id='snippet-sample_replacing_handler_discovery_rules'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        // Turn off Wolverine's built in handler discovery
        opts.DisableConventionalDiscovery();
        
        // And replace the scanning with your own special discovery:
        opts.Discovery.CustomizeHandlerDiscovery(q =>
        {
            q.Includes.WithNameSuffix("Listener");
        });
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/Configuration/HandlerDiscoveryTests.cs#L36-L51' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_replacing_handler_discovery_rules' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/Configuration/find_handlers_with_the_default_handler_discovery.cs#L237-L285' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_wolverineignoreattribute' title='Start of snippet'>anchor</a></sup>
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
        opts.Discovery

            // Turn off the default handler conventions
            // altogether
            .DisableConventionalDiscovery()

            // Include candidate actions by a user supplied
            // type filter
            .CustomizeHandlerDiscovery(x =>
            {
                x.Includes.WithNameSuffix("Worker");
                x.Includes.WithNameSuffix("Listener");
            })

            // Include a specific handler class with a generic argument
            .IncludeType<SimpleHandler>();
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/HandlerDiscoverySamples.cs#L120-L143' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_customhandlerapp' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->
