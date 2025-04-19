# Middleware

::: tip
One of the big advantages of Wolverine's middleware model as compared to almost any other .NET application framework is that middleware can be selectively applied to only certain
message handlers or HTTP endpoints. When you craft your middleware, try to take advantage of this to avoid unnecessary runtime logic in middleware (i.e., for example, don't use Reflection or optional
IoC service registrations to "decide" if middleware applies to the current HTTP request or message).
:::

Wolverine supports the "Russian Doll" model of middleware, similar in concept to ASP.NET Core but very different in implementation. 
Wolverine's middleware uses runtime code generation and compilation with [JasperFx.CodeGeneration](https://github.com/jasperfx/jasperfx.codegeneration) (which is also used by [Marten](https://martendb.io)). 
What this means is that "middleware" in Wolverine is code that is woven right into the message and route handlers. The end result is a much more efficient runtime pipeline
than most other frameworks that adopt the "Russian Doll" middleware approach that suffer performance issues because of the sheer number of object allocations. It also hopefully means
that the exception stack traces from failures in Wolverine message handlers will be far less noisy than competitor tools and Wolverine's own predecessors.

::: tip
Wolverine has [performance metrics](/guide/logging) around message execution out of the box, so this whole "stopwatch" sample is unnecessary. But it *was* an easy way to illustrate
the middleware approach.
:::

As an example, let's say you want to build some custom middleware that is a simple performance timing of either HTTP route execution or message execution. In essence, you want to inject code like this:

<!-- snippet: sample_stopwatch_concept -->
<a id='snippet-sample_stopwatch_concept'></a>
```cs
var stopwatch = new Stopwatch();
stopwatch.Start();
try
{
    // execute the HTTP request
    // or message
}
finally
{
    stopwatch.Stop();
    logger.LogInformation("Ran something in " + stopwatch.ElapsedMilliseconds);
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/Middleware.cs#L20-L35' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_stopwatch_concept' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

You've got a couple different options, but the easiest by far is to use Wolverine's conventional middleware approach.

## Conventional Middleware

::: info
Conventional application of middleware is done separately between HTTP endpoints and message handlers. To apply global middleware
to HTTP endpoints, see [HTTP endpoint middleware](/guide/http/middleware).
:::

As an example middleware using Wolverine's conventional approach, here's the stopwatch functionality from above:

<!-- snippet: sample_StopwatchMiddleware_1 -->
<a id='snippet-sample_stopwatchmiddleware_1'></a>
```cs
public class StopwatchMiddleware
{
    private readonly Stopwatch _stopwatch = new();

    public void Before()
    {
        _stopwatch.Start();
    }

    public void Finally(ILogger logger, Envelope envelope)
    {
        _stopwatch.Stop();
        logger.LogDebug("Envelope {Id} / {MessageType} ran in {Duration} milliseconds",
            envelope.Id, envelope.MessageType, _stopwatch.ElapsedMilliseconds);
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/Middleware.cs#L72-L91' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_stopwatchmiddleware_1' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

and that can be added to our application at bootstrapping time like this:

<!-- snippet: sample_applying_middleware_by_policy -->
<a id='snippet-sample_applying_middleware_by_policy'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        // Apply our new middleware to message handlers, but optionally
        // filter it to only messages from a certain namespace
        opts.Policies
            .AddMiddleware<StopwatchMiddleware>(chain =>
                chain.MessageType.IsInNamespace("MyApp.Messages.Important"));
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/Middleware.cs#L138-L150' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_applying_middleware_by_policy' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

And just for the sake of completeness, here's another version of the same functionality, but 
this time using a static class *just* to save on object allocations:

<!-- snippet: sample_silly_micro_optimized_stopwatch_middleware -->
<a id='snippet-sample_silly_micro_optimized_stopwatch_middleware'></a>
```cs
public static class StopwatchMiddleware2
{
    // The Stopwatch being returned from this method will
    // be passed back into the later method
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Stopwatch Before()
    {
        var stopwatch = new Stopwatch();
        stopwatch.Start();

        return stopwatch;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Finally(Stopwatch stopwatch, ILogger logger, Envelope envelope)
    {
        stopwatch.Stop();
        logger.LogDebug("Envelope {Id} / {MessageType} ran in {Duration} milliseconds",
            envelope.Id, envelope.MessageType, stopwatch.ElapsedMilliseconds);
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/Middleware.cs#L108-L132' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_silly_micro_optimized_stopwatch_middleware' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Alright, let's talk about what's happening in the code samples above:

* You'll notice that I took in `ILogger` instead of any specific `ILogger<T>`. Wolverine is quietly using the `ILogger<Message Type>` for the current handler when it generates the code. 
* Wolverine places the `Before()` method to be called in front of the actual message handler method
* Because there is a `Finally()` method, Wolverine wraps a `try/finally` block around the code running after the `Before()` method and calls `Finally()` within that `finally` block

::: tip
Note that the method name matching is case sensitive.
:::

Here's the conventions:

| Lifecycle                                                | Method Names                |
|----------------------------------------------------------|-----------------------------|
| Before the Handler(s)                                    | `Before`, `BeforeAsync`, `Load`, `LoadAsync`, `Validate`, `ValidateAsync` |
| After the Handler(s)                                     | `After`, `AfterAsync`, `PostProcess`, `PostProcessAsync` |
| In `finally` blocks after the Handlers & "After" methods | `Finally`, `FinallyAsync`   |

The generated code for the conventionally applied methods would look like this basic structure:

<!-- snippet: sample_demonstrating_middleware_application -->
<a id='snippet-sample_demonstrating_middleware_application'></a>
```cs
middleware.Before();
try
{
    // call the actual handler methods
    middleware.After();
}
finally
{
    middleware.Finally();
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/Middleware.cs#L40-L53' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_demonstrating_middleware_application' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Here's the rules for these conventional middleware classes:

* Can optionally be static classes, and that maybe advantageous when possible from a performance standpoint
* If the middleware class is not static, Wolverine can inject constructor arguments with the same rules as for [handler methods](/guide/handlers/)
* Objects returned from the `Before` / `BeforeAsync` methods can be used as arguments to the inner handler methods or the later "after" or "finally" methods
* A middleware class can have any mix of zero to many "befores", "afters", or "finallys."

## Conditionally Stopping the Message Handling

A "before" method in middleware can be used to stop further message handler by either directly returning `HandlerContinuation` or returning that value as part of a
tuple. If the value `Stop` is returned, Wolverine will stop all of the further message processing (it's done by generating an `if (continuation == HandlerContinuation.Stop) return;` line of code).

Here's an example from the [custom middleware tutorial](/tutorials/middleware) that tries to load a matching `Account` entity referenced
by the incoming message and aborts the message processing if it is not found:

<!-- snippet: sample_AccountLookupMiddleware -->
<a id='snippet-sample_accountlookupmiddleware'></a>
```cs
// This is *a* way to build middleware in Wolverine by basically just
// writing functions/methods. There's a naming convention that
// looks for Before/BeforeAsync or After/AfterAsync
public static class AccountLookupMiddleware
{
    // The message *has* to be first in the parameter list
    // Before or BeforeAsync tells Wolverine this method should be called before the actual action
    public static async Task<(HandlerContinuation, Account?, OutgoingMessages)> LoadAsync(
        IAccountCommand command,
        ILogger logger,

        // This app is using Marten for persistence
        IDocumentSession session,

        CancellationToken cancellation)
    {
        var messages = new OutgoingMessages();
        var account = await session.LoadAsync<Account>(command.AccountId, cancellation);
        if (account == null)
        {
            logger.LogInformation("Unable to find an account for {AccountId}, aborting the requested operation", command.AccountId);

            messages.RespondToSender(new InvalidAccount(command.AccountId));
            return (HandlerContinuation.Stop, null, messages);
        }

        // messages would be empty here
        return (HandlerContinuation.Continue, account, messages);
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/Middleware/AppWithMiddleware/Account.cs#L78-L111' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_accountlookupmiddleware' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Notice that the middleware above uses a tuple as the return value so that it can both pass an `Account` entity to the inner handler and also
to return the continuation directing Wolverine to continue or stop the message processing. 

## Sending Messages From Middleware

::: tip
Everything shown here works for both middleware methods on external types that are applied to the message handlers,
or to 
:::

::: warning
This will not work for WolverineFx.Http endpoints, but at least there, you'd probably be better served through
returning a `ProblemDetails` response or some other error response to the original caller.
:::

Wolverine *can* send outgoing messages from middleware. You can use either `IMessageBus` directly as shown below:

<!-- snippet: sample_sending_messages_in_before_middleware -->
<a id='snippet-sample_sending_messages_in_before_middleware'></a>
```cs
public static class MaybeBadThingHandler
{
    public static async Task<HandlerContinuation> ValidateAsync(MaybeBadThing thing, IMessageBus bus)
    {
        if (thing.Number > 10)
        {
            await bus.PublishAsync(new RejectYourThing(thing.Number));
            return HandlerContinuation.Stop;
        }

        return HandlerContinuation.Continue;
    }

    public static void Handle(MaybeBadThing message)
    {
        Debug.WriteLine("Got " + message);
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/Acceptance/compound_handlers.cs#L134-L155' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_sending_messages_in_before_middleware' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Or by returning `OutgoingMessages` from a middleware method as shown below:

<!-- snippet: sample_using_outgoing_messages_from_before_middleware -->
<a id='snippet-sample_using_outgoing_messages_from_before_middleware'></a>
```cs
public static class MaybeBadThing2Handler
{
    public static (HandlerContinuation, OutgoingMessages) ValidateAsync(MaybeBadThing2 thing, IMessageBus bus)
    {
        if (thing.Number > 10)
        {
            return (HandlerContinuation.Stop, [new RejectYourThing(thing.Number)]);
        }

        return (HandlerContinuation.Continue, []);
    }

    public static void Handle(MaybeBadThing2 message)
    {
        Debug.WriteLine("Got " + message);
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/Acceptance/compound_handlers.cs#L157-L177' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_outgoing_messages_from_before_middleware' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->



## Registering Middleware by Message Type

Let's say that some of our message types implement this interface:

<!-- snippet: sample_IAccountCommand -->
<a id='snippet-sample_iaccountcommand'></a>
```cs
public interface IAccountCommand
{
    Guid AccountId { get; }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/Middleware/AppWithMiddleware/Account.cs#L36-L43' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_iaccountcommand' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

We can apply the `AccountMiddleware` from the section above to only these message types by telling Wolverine to only apply this middleware 
to any message that implements the `IAccountCommand` interface like this:

<!-- snippet: sample_registering_middleware_by_message_type -->
<a id='snippet-sample_registering_middleware_by_message_type'></a>
```cs
builder.Host.UseWolverine(opts =>
{
    // This middleware should be applied to all handlers where the
    // command type implements the IAccountCommand interface that is the
    // "detected" message type of the middleware
    opts.Policies.ForMessagesOfType<IAccountCommand>().AddMiddleware(typeof(AccountLookupMiddleware));

    opts.UseFluentValidation();

    // Explicit routing for the AccountUpdated
    // message handling. This has precedence over conventional routing
    opts.PublishMessage<AccountUpdated>()
        .ToLocalQueue("signalr")

        // Throw the message away if it's not successfully
        // delivered within 10 seconds
        .DeliverWithin(10.Seconds())

        // Not durable
        .BufferedInMemory();
});
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/Middleware/AppWithMiddleware/Program.cs#L30-L54' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_registering_middleware_by_message_type' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Wolverine determines the message type for a middleware class method by assuming that the first
argument is the message type, and then looking for actual messages that implement that interface or
subclass.

## Applying Middleware Explicitly by Attribute

::: tip
You can subclass the `MiddlewareAttribute` class to make more specific middleware applicative attributes for your application. 
:::

You can apply the middleware types to individual handler methods with the `[Middleware]` attribute as shown below:

<!-- snippet: sample_apply_middleware_by_attribute -->
<a id='snippet-sample_apply_middleware_by_attribute'></a>
```cs
public static class SomeHandler
{
    [Middleware(typeof(StopwatchMiddleware))]
    public static void Handle(PotentiallySlowMessage message)
    {
        // do something expensive with the message
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/Middleware.cs#L95-L106' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_apply_middleware_by_attribute' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Note that this attribute will accept multiple middleware types. Also note that the `[Middleware]` attribute can be placed either
on an individual handler method or apply to all handler methods on the same handler class if the attribute is at the class level.


## Custom Code Generation

For more advanced usage, you can drop down to the JasperFx.CodeGeneration `Frame` model to directly inject code.

The first step is to create a JasperFx.CodeGeneration `Frame` class that generates that code around the inner message or HTTP handler:

<!-- snippet: sample_StopwatchFrame -->
<a id='snippet-sample_stopwatchframe'></a>
```cs
public class StopwatchFrame : SyncFrame
{
    private readonly IChain _chain;
    private readonly Variable _stopwatch;
    private Variable _logger;

    public StopwatchFrame(IChain chain)
    {
        _chain = chain;

        // This frame creates a Stopwatch, so we
        // expose that fact to the rest of the generated method
        // just in case someone else wants that
        _stopwatch = new Variable(typeof(Stopwatch), "stopwatch", this);
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.Write($"var stopwatch = new {typeof(Stopwatch).FullNameInCode()}();");
        writer.Write("stopwatch.Start();");

        writer.Write("BLOCK:try");
        Next?.GenerateCode(method, writer);
        writer.FinishBlock();

        // Write a finally block where you record the stopwatch
        writer.Write("BLOCK:finally");

        writer.Write("stopwatch.Stop();");
        writer.Write(
            $"{_logger.Usage}.Log(Microsoft.Extensions.Logging.LogLevel.Information, \"{_chain.Description} ran in \" + {_stopwatch.Usage}.{nameof(Stopwatch.ElapsedMilliseconds)});)");

        writer.FinishBlock();
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        // This in effect turns into "I need ILogger<message type> injected into the
        // compiled class"
        _logger = chain.FindVariable(typeof(ILogger));
        yield return _logger;
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/Middleware.cs#L154-L200' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_stopwatchframe' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->


## Custom Attributes

To attach our `StopwatchFrame` as middleware to any route or message handler, we can write a custom attribute based on Wolverine's
`ModifyChainAttribute` class as shown below:

<!-- snippet: sample_StopwatchAttribute -->
<a id='snippet-sample_stopwatchattribute'></a>
```cs
public class StopwatchAttribute : ModifyChainAttribute
{
    public override void Modify(IChain chain, GenerationRules rules, IServiceContainer container)
    {
        chain.Middleware.Add(new StopwatchFrame(chain));
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/Middleware.cs#L202-L212' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_stopwatchattribute' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

This attribute can now be placed either on a specific HTTP route endpoint method or message handler method to **only** apply to
that specific action, or it can be placed on a `Handler` or `Endpoint` class to apply to all methods exported by that type.

Here's an example:

<!-- snippet: sample_ClockedEndpoint -->
<a id='snippet-sample_clockedendpoint'></a>
```cs
public class ClockedEndpoint
{
    [Stopwatch]
    public string get_clocked()
    {
        return "how fast";
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/Middleware.cs#L214-L225' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_clockedendpoint' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Now, when the application is bootstrapped, this is the code that would be generated to handle the "GET /clocked" route:

```csharp
public class Wolverine_Testing_Samples_ClockedEndpoint_get_clocked : Wolverine.Http.Model.RouteHandler
{
    private readonly Microsoft.Extensions.Logging.ILogger<Wolverine.Configuration.IChain> _logger;

    public Wolverine_Testing_Samples_ClockedEndpoint_get_clocked(Microsoft.Extensions.Logging.ILogger<Wolverine.Configuration.IChain> logger)
    {
        _logger = logger;
    }

    public override Task Handle(Microsoft.AspNetCore.Http.HttpContext httpContext, System.String[] segments)
    {
        var clockedEndpoint = new Wolverine.Testing.Samples.ClockedEndpoint();
        var stopwatch = new System.Diagnostics.Stopwatch();
        stopwatch.Start();
        try
        {
            var result_of_get_clocked = clockedEndpoint.get_clocked();
            return WriteText(result_of_get_clocked, httpContext.Response);
        }

        finally
        {
            stopwatch.Stop();
            _logger.Log(Microsoft.Extensions.Logging.LogLevel.Information, "Route 'GET: clocked' ran in " + stopwatch.ElapsedMilliseconds);)
        }

    }

}
```

`ModifyChainAttribute` is a generic way to add middleware or post processing frames, but if you need to configure things specific to routes or message handlers, you can also use `ModifyHandlerChainAttribute` for message handlers or `ModifyRouteAttribute` for http routes.


## Policies

::: tip warning
Again, please go easy with this feature and try not to shoot yourself in the foot by getting too aggressive with custom policies
:::

You can register user-defined policies that apply to all chains or some subset of chains. For message handlers, implement this interface:

<!-- snippet: sample_IHandlerPolicy -->
<a id='snippet-sample_ihandlerpolicy'></a>
```cs
/// <summary>
///     Use to apply your own conventions or policies to message handlers
/// </summary>
public interface IHandlerPolicy : IWolverinePolicy
{
    /// <summary>
    ///     Called during bootstrapping to alter how the message handlers are configured
    /// </summary>
    /// <param name="chains"></param>
    /// <param name="rules"></param>
    /// <param name="container">The application's underlying IoC Container</param>
    void Apply(IReadOnlyList<HandlerChain> chains, GenerationRules rules, IServiceContainer container);
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Wolverine/Configuration/IHandlerPolicy.cs#L36-L52' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_ihandlerpolicy' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Here's a simple sample that registers middleware on each handler chain:

<!-- snippet: sample_WrapWithSimple -->
<a id='snippet-sample_wrapwithsimple'></a>
```cs
public class WrapWithSimple : IHandlerPolicy
{
    public void Apply(IReadOnlyList<HandlerChain> chains, GenerationRules rules, IServiceContainer container)
    {
        foreach (var chain in chains) chain.Middleware.Add(new SimpleWrapper());
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/BootstrappingSamples.cs#L59-L69' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_wrapwithsimple' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Then register your custom `IHandlerPolicy` with a Wolverine application like this:

<!-- snippet: sample_AppWithHandlerPolicy -->
<a id='snippet-sample_appwithhandlerpolicy'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts => { opts.Policies.Add<WrapWithSimple>(); }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/BootstrappingSamples.cs#L15-L20' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_appwithhandlerpolicy' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Using Configure(chain) Methods

::: tip warning
This feature is experimental, but is meant to provide an easy way to apply middleware or other configuration to specific HTTP endpoints or
message handlers without writing custom policies or having to resort to all new attributes.
:::

There's one last option for configuring chains by a naming convention. If you want to configure the chains from just one handler or endpoint class,
you can implement a method with one of these signatures:

```csharp
public static void Configure(IChain)
{
    // gets called for each endpoint or message handling method
    // on just this class
}

public static void Configure(RouteChain chain)
{
    // gets called for each endpoint method on this class
}

public static void Configure(HandlerChain chain)
{
    // gets called for each message handling method
    // on just this class
}
```

Here's an example of this being used from Wolverine's test suite:

<!-- snippet: sample_customized_handler_using_Configure -->
<a id='snippet-sample_customized_handler_using_configure'></a>
```cs
public class CustomizedHandler
{
    public void Handle(SpecialMessage message)
    {
        // actually handle the SpecialMessage
    }

    public static void Configure(HandlerChain chain)
    {
        chain.Middleware.Add(new CustomFrame());

        // Turning off all execution tracking logging
        // from Wolverine for just this message type
        // Error logging will still be enabled on failures
        chain.SuccessLogLevel = LogLevel.None;
        chain.ProcessingLogLevel = LogLevel.None;
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/Configuration/can_customize_handler_chain_through_Configure_call_on_HandlerType.cs#L25-L46' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_customized_handler_using_configure' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->



