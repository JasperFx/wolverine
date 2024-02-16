# Best Practices

The Wolverine community certainly wants you to be successful with Wolverine, so we're using this page to gather whatever
advice we can offer. This advice falls into two areas, generic tips for creating maintainable code when using asynchronous
messaging that would apply to any messaging or command executor tool, and specific tips for Wolverine itself.

## Dividing Handlers

Wolverine does not enforce or require today any kind of explicit assignment of incoming message handler to endpoint (i.e. 
Rabbit MQ queue, AWS SQS queue, Kafka topic). That being said, you will frequently want to define your message routing
to isolate incoming message types into separate endpoints for the sake of throughput or parallelized work. Likewise, if 
message ordering is important, you may want to purposely route multiple message types to the same listening endpoint. 

While Wolverine will happily allow you to implement multiple message handler methods or multiple HTTP endpoints in the same
class, you may get better results by only allowing a single message handler method per class. Especially for larger, 
more complex message handling or HTTP request handling. 

## Avoid Abstracting Wolverine 

This is generic advice for just about any infrastructure tool. You will lose out on Wolverine functionality by trying
to abstract it, and very likely just create an abstraction that merely mimics a subset of Wolverine for little gain. 

If you are concerned about testability of your message handlers, we recommend using [cascading messages](/guide/handlers/cascading) instead
anyway that would completely remove the need for abstracting Wolverine. 

## Lean on Wolverine Error Handling

Rather than explicitly catching exceptions in message handlers, we recommend leaning on Wolverine's configurable error
handling policies. This will save you explicit code that can obfuscate your actual functionality, while still providing
robust responses to errors and Wolverine's built in observability (error logging, circuit breakers, execution statistics).

## Pre-Generate Types to Optimize Production Usage

Wolverine has admittedly an unusual runtime architecture in that it depends much more on runtime generated code than
the IoC container tricks that many other .NET frameworks do today. That's great for performance, and definitely
helps Wolverine to enable much lower ceremony code, but that comes with a potentially significant memory usage and [cold start](https://en.wikipedia.org/wiki/Cold_start_(computing)#:~:text=Cold%20start%20in%20computing%20refers,cache%20or%20starting%20up%20subsystems.)
problem.

If you see any of the issues I just described, or want to get in front of this issue, utilize the "pre-generated types"
functionality described in [Working with Code Generation](/guide/codegen).

## Prefer Pure Functions for Business Logic

As much as possible, we recommend that you try to create [pure functions](https://en.wikipedia.org/wiki/Pure_function) for any business logic or workflow routing logic that is responsible for 
"deciding" what to do next. The goal here is to make that code relatively easy to test inside of isolated unit tests that
are completely decoupled from infrastructure. Moreover, using pure functions allows you to largely eschew the usage of mock
objects inside of unit tests which can become problematic when overused.

Wolverine has a lot of specific functionality to move infrastructure concerns out of the way of your business or workflow 
logic. For tips on how to create pure functions for your Wolverine message handlers or HTTP endpoints, see:

* [A-Frame Architecture with Wolverine](https://jeremydmiller.com/2023/07/19/a-frame-architecture-with-wolverine/)
* [Testing Without Mocks: A Pattern Language by Jim Shore](https://www.jamesshore.com/v2/projects/nullables/testing-without-mocks)
* [Compound Handlers in Wolverine](https://jeremydmiller.com/2023/03/07/compound-handlers-in-wolverine/)
* [Isolating Side Effects from Wolverine Handlers](https://jeremydmiller.com/2023/04/24/isolating-side-effects-from-wolverine-handlers/)


## Only Publish Messages from the Root Handler Method

Very frequently, you'll need to publish additional messages from either an HTTP endpoint or a message handler. You can technically
have the current `IMessageBus` for the message injected into your handler's dependencies and have outgoing messages published
deeper in your call stack -- but we strongly recommend not doing that **because it can make your code and your system hard to reason
about**. 

Instead, as much as possible, the Wolverine team recommends that these outgoing, "cascading" messages only be published from the root method
like shown below:

```csharp
// Using cascading messages is certainly fine
public static SecondMessage Handle(FirstMessage message, IService1 service1)
{
    return new SecondMessage();
}

// This is fine too if you prefer the more explicit code model and don't mind
// the tighter coupling
public static async ValueTask<SecondMessage> Handle(FirstMessage message, IService1 service1, IMessageBus bus)
{
    // Little more coupling, but some folks will prefer the more explicit style
    await bus.PublishAsync(new SecondMessage());
}


public static SecondMessage? Handle(FirstMessage message, IService1 service1)
{
    // Call into another service to *decide* whether or not to send
    // the cascading service
    if (service1.ComplicatedLogicTest(message))
    {
        return BuildUpComplicatedMessage();
    }
    
    return null; // no cascading message
}
```

Consider this case as an anti-pattern to avoid:

1. Your message handler method calls a method on `IService1`
2. Which might call a method on `IService2`
3. Which might call `IMessageBus.PublishAsync()` to publish a new message as part of you original message handling

In the case above, it can become very easy to lose sight of the workflow of the system, and the Wolverine team has
encountered systems build using other messaging frameworks that suffered from this problem. 




## Keep Your Call Stacks Short


## Attaining IMessageBus

The Wolverine team recommendation is to utilize [cascading messages](/guide/handlers/cascading) as much as possible to publish additional messages,
but when you do need to attain a reference to an `IMessageBus` object, we suggest to take that service into your
message handler or HTTP endpoint methods as a method argument like so:

```csharp
public static async Task HandleAsync(MyMessage message, IMessageBus messageBus)
{
    // handle the message..;
}
```

or if you really prefer the little extra ceremony of constructor injection because that's how the .NET ecosystem has
worked for the past 15-20 years, do this:

```csharp

public class MyMessageHandler
{
    private readonly IMessageBus _messageBus;

    public MyMessageHandler(IMessageBus messageBus)
    {
        _messageBus = messageBus;
    }
    
    public async Task HandleAsync(MyMessage message, IMessageBus messageBus)
    {
         // handle the message..;
    }
}
```

Avoid ever trying to resolve `IMessageBus` at runtime through a scoped container like this:

```csharp
services.AddScoped<IService>(s => {
    var bus = s.GetRequiredService<IMessageBus>();
    return new Service { TenantId = bus.TenantId };
});
```

The usage above will give you a completely different `IMessageBus` object than the current `MessageContext` being used
by Wolverine to track behavior and state.

## IoC Container Usage

::: tip
Wolverine is trying really hard **not** to use an IoC container whatsoever at runtime. It's not going to work with 
Wolverine to try to pass state between the outside world into Wolverine through ASP.Net Core scoped containers for
example.
:::

Honestly, you'll get better performance and better results all the way around if you can avoid doing any kind of "opaque"
service registrations in your IoC container that require runtime resolution. In effect, this means to stay away from 
any kind of `Scoped` or `Transient` Lambda registration like:

```csharp
services.AddScoped<IDatabase>(s => {
    var c = s.GetRequiredService<IConfiguration>();
    return new Database(c.GetConnectionString("foo");
});
```

This might be more about preference than a hard advantage (it is a performance improvement though), but the Wolverine team
recommends using method injection over the older, traditional constructor injection approach as shown below:

```csharp
// Wolverine prefers this:
public static class Message1Handler
{
    public static Task HandleAsync(Message1 message, IService service)
    {
        // Do stuff
    }
}


// This certainly works with Wolverine, but it's more code and more
// runtime overhead
public class Message1Handler
{
    private readonly IService1 _service1;
    
    public Message1Handler(IService1 service1)
    {
        _service1 = service1;
    }
    
    public Task HandleAsync(Message1 message)
    {
        // Do stuff
    }
}
```

By and large, Wolverine kind of wants you to use fewer abstractions and keep a shorter call stack. See the earlier section
about "pure function" handlers for alternatives to jamming more abstracted services into an IoC container in order to 
create separation of concerns and testability.

## Vertical Slice Architecture

::: tip
Despite its name, "Vertical Slice Architecture" is really just an idea about organizing code and not what I would normally
think of as a true architectural pattern. You could technically follow any kind of ports and adapter style of coding like
the Clean Architecture while still organizing your code in vertical slices instead of horizontal layers. 
:::

There's nothing stopping you from using Wolverine as part of a typical [Clean Architecture](https://www.youtube.com/watch?v=yF9SwL0p0Y0) or [Onion Architecture](https://jeffreypalermo.com/2008/07/the-onion-architecture-part-1/)
project layout where technical concerns are generally spread out into different projects per technical concern. Wolverine though
has quite a bit of features specifically to support a "Vertical Slice Architecture" code layout, and you may be able
to utilize Wolverine to create a maintainable codebase with much less complexity than the state of the art Clean/Onion
layered approach.

See [Low Ceremony Vertical Slice Architecture with Wolverine](https://jeremydmiller.com/2023/07/10/low-ceremony-vertical-slice-architecture-with-wolverine/)
