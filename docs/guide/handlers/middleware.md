# Middleware

Wolverine supports the "Russian Doll" model of middleware, similar in concept to ASP.NET Core but very different in implementation. 
Wolverine's middleware uses runtime code generation and compilation with [JasperFx.CodeGeneration](https://github.com/jasperfx/jasperfx.codegeneration) (which is also used by [Marten](https://martendb.io)). 
What this means is that "middleware" in Wolverine is code that is woven right into the message and route handlers. The end result is a much more efficient runtime pipeline
than most other frameworks that adopt the "Russian Doll" middleware approach that suffer performance issues because of the sheer number of object allocations. It also hopefully means
that the exception stack traces from failures in Wolverine message handlers will be far less noisy than competitor tools and Wolverine's own predecessors.

::: tip
Wolverine has [performance metrics](/guide/logging) around message execution out of the box, so this while "stopwatch" sample is unnecessary. But it *was* an easy way to illustrate
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
    logger.Info("Ran something in " + stopwatch.ElapsedMilliseconds);
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/Middleware.cs#L17-L32' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_stopwatch_concept' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

You've got a couple different options, but the easiest by far is to use Wolverine's conventional middleware approach.

## Conventional Middleware

As an example middleware using Wolverine's conventional approach, here's the stopwatch functionality from above:

snippet: sample_StopwatchMiddleware_1

and that can be added to our application at bootstrapping time like this:

snippet: sample_applying_middleware_by_policy

And just for the sake of completeness, here's another version of the same functionality, but 
this time using a static class *just* to save on object allocations:

snippet: sample_silly_micro_optimized_stopwatch_middleware

Alright, let's talk about what's happening in the code samples above:

* You'll notice that I took in `ILogger` instead of any specific `ILogger<T>`. Wolverine is quietly using the `ILogger<Message Type>` for the current handler when it generates the code. 
* Wolverine places the `Before()` method to be called in front of the actual message handler method
* Because there is a `Finally()` method, Wolverine wraps a `try/finally` block around the code running after the `Before()` method and calls `Finally()` within that `finally` block

::: 
Note that the method name matching is case sensitive.
:::

Here's the conventions:

| Lifecycle                                                | Method Names                |
|----------------------------------------------------------|-----------------------------|
| Before the Handler(s)                                    | `Before`, `BeforeAsync`, `Load`, `LoadAsync` |
| After the Handler(s)                                     | `After`, `AfterAsync`, `PostProcess`, `PostProcessAsync` |
| In `finally` blocks after the Handlers & "After" methods | `Finally`, `FinallyAsync`   |

The generated code for the conventionally applied methods would look like this basic structure:

snippet: sample_demonstrating_middleware_application

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

snippet: sample_AccountLookupMiddleware

Notice that the middleware above uses a tuple as the return value so that it can both pass an `Account` entity to the inner handler and also
to return the continuation directing Wolverine to continue or stop the message processing. 

## Registering Middleware by Message Type

Let's say that some of our message types implement this interface:

snippet: sample_IAccountCommand

We can apply the `AccountMiddleware` from the section above to only these message types by telling Wolverine to only apply this middleware 
to any message that implements the `IAccountCommand` interface like this:

snippet: sample_registering_middleware_by_message_type

Wolverine determines the message type for a middleware class method by assuming that the first
argument is the message type, and then looking for actual messages that implement that interface or
subclass.


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
        // This in effect turns into "I need ILogger<IChain> injected into the
        // compiled class"
        _logger = chain.FindVariable(typeof(ILogger<IChain>));
        yield return _logger;
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/Middleware.cs#L36-L83' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_stopwatchframe' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->



## Applying Middleware

Okay, great, but the next question is "how do I stick this middleware on routes or message handlers?". You've got three options:

1. Use custom attributes
1. Use a custom `IRoutePolicy` or `IHandlerPolicy` class
1. Expose a static `Configure(chain)` method on handler classes

Even though one of the original design goals of FubuMVC and now Wolverine was to eliminate or at least reduce the number of attributes users had to spew out into their application code, let's start with using an attribute.

## Custom Attributes

To attach our `StopwatchFrame` as middleware to any route or message handler, we can write a custom attribute based on Wolverine's
`ModifyChainAttribute` class as shown below:

<!-- snippet: sample_StopwatchAttribute -->
<a id='snippet-sample_stopwatchattribute'></a>
```cs
public class StopwatchAttribute : ModifyChainAttribute
{
    public override void Modify(IChain chain, GenerationRules rules, IContainer container)
    {
        chain.Middleware.Add(new StopwatchFrame(chain));
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/Middleware.cs#L85-L95' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_stopwatchattribute' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/Middleware.cs#L97-L108' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_clockedendpoint' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Now, when the application is bootstrapped, this is the code that would be generated to handle the "GET /clocked" route:

```
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
public interface IHandlerPolicy
{
    /// <summary>
    ///     Called during bootstrapping to alter how the message handlers are configured
    /// </summary>
    /// <param name="graph"></param>
    /// <param name="rules"></param>
    /// <param name="container">The application's underlying Lamar Container</param>
    void Apply(HandlerGraph graph, GenerationRules rules, IContainer container);
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Wolverine/Configuration/IHandlerPolicy.cs#L7-L23' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_ihandlerpolicy' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Here's a simple sample that registers middleware on each handler chain:

<!-- snippet: sample_WrapWithSimple -->
<a id='snippet-sample_wrapwithsimple'></a>
```cs
public class WrapWithSimple : IHandlerPolicy
{
    public void Apply(HandlerGraph graph, GenerationRules rules, IContainer container)
    {
        foreach (var chain in graph.Chains) chain.Middleware.Add(new SimpleWrapper());
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/BootstrappingSamples.cs#L24-L34' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_wrapwithsimple' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Then register your custom `IHandlerPolicy` with a Wolverine application like this:

<!-- snippet: sample_AppWithHandlerPolicy -->
<a id='snippet-sample_appwithhandlerpolicy'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts => { opts.Handlers.AddPolicy<WrapWithSimple>(); }).StartAsync();
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

```
public static void Configure(IChain)
{
    // gets called for each endpoint or message handling method
    // on just this class
}

public static void Configure(RouteChain chain)`
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
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/Configuration/can_customize_handler_chain_through_Configure_call_on_HandlerType.cs#L28-L43' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_customized_handler_using_configure' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->



