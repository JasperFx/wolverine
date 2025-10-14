# Dealing with Concurrency

![Lions and tigers and bears, oh my!](/wolverines-wizard-of-oz.png)

With a little bit of research today -- and unfortunately my own experience -- here's a list of *some* of the problems that
can be caused by concurrent message processing in your system trying to access or modify the same resources or data:

* Race conditions
* [Deadlocks](https://en.wikipedia.org/wiki/Deadlock)
* Consistency errors when multiple threads may be overwriting the same data and some changes get lost
* Out of order processing that may lead to erroneous results
* Exceptions from tools like Marten that helpfully try to stop concurrent changes through [optimistic concurrency](https://en.wikipedia.org/wiki/Optimistic_concurrency_control)

Because these issues are so common in the kind of systems you would want to use a tool like Wolverine on in the first place,
the Wolverine community has invested quite heavily in features to help you manage concurrent access in your system. 

## Error Retries on Concurrency Errors

If you don't expect many concurrency exceptions, you can probably get away with some kind of optimistic concurrency. Using
the [aggregate handler workflow](/guide/durability/marten/event-sourcing) integration with Marten as an example, there is some built in optimistic concurrency
in Marten just to protect your system from simultaneous writes to the same event stream. In the case when Marten determines
that *something* else has written to an event stream between your command handling starting and it trying to commit changes,
Marten will throw the `JasperFx.ConcurrencyException`.

If we're doing simplistic optimistic checks, we might be perfectly fine with a global error handler that simply [retries
any failure](/guide/handlers/error-handling) due to this exception a few times:

<!-- snippet: sample_simple_retries_on_concurrency_exception -->
<a id='snippet-sample_simple_retries_on_concurrency_exception'></a>
```cs
var builder = Host.CreateApplicationBuilder();
builder.UseWolverine(opts =>
{
    opts
        // On optimistic concurrency failures from Marten
        .OnException<ConcurrencyException>()
        .RetryWithCooldown(100.Milliseconds(), 250.Milliseconds(), 500.Milliseconds())
        .Then.MoveToErrorQueue();
});
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/ExceptionHandling.cs#L15-L27' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_simple_retries_on_concurrency_exception' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Of course though, sometimes you are opting into a more stringent form of optimistic concurrency where the handler should
fail fast if an event stream has advanced beyond a specific version number, as in the usage of this command message:

```csharp
public record MarkItemReady(Guid OrderId, string ItemName, int Version);
```

In that case, there's absolutely no value in retrying the message, so we should use a different error handling policy to
move that message off immediately like one of these:

<!-- snippet: sample_showing_concurrency_exception_moving_directly_to_DLQ -->
<a id='snippet-sample_showing_concurrency_exception_moving_directly_to_dlq'></a>
```cs
public static class MarkItemReadyHandler
{
    // This will let us specify error handling policies specific
    // to only this message handler
    public static void Configure(HandlerChain chain)
    {
        // Can't ever process this message, so send it directly 
        // to the DLQ
        // Do not pass Go, do not collect $200...
        chain.OnException<ConcurrencyException>()
            .MoveToErrorQueue();
        
        // Or instead...
        // Can't ever process this message, so just throw it away
        // Do not pass Go, do not collect $200...
        chain.OnException<ConcurrencyException>()
            .Discard();
    }
    
    public static IEnumerable<object> Post(
        MarkItemReady command, 
        
        // Wolverine + Marten will assert that the Order stream
        // in question has not advanced from command.Version
        [WriteAggregate] Order order)
    {
        // process the message and emit events
        yield break;
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Marten/Orders.cs#L372-L405' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_showing_concurrency_exception_moving_directly_to_dlq' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Exclusive Locks or Serializable Transactions

You can try to deal with concurrency problems by utilizing whatever database tooling you're using for
whatever exclusive locks or serializable transaction support they might have. The integration with Marten has
an option for exclusive locks with the "Aggregate Handler Workflow." With EF Core, you should be able to opt into starting
your own serializable transaction.

The Wolverine team considers these approaches to maybe a necessary evil, but hopefully a temporary solution. We would
probably recommend in most cases that you protect your system from concurrent access through selective queueing as much as
possible as discussed in the next section.

## Using Queueing

In many cases you can use queueing of some sort to reduce concurrent access to sensitive resources within your system.
The most draconian way to do this is to say that all messages in a given queue will be executed single file in strict
order on one single node within your application like so:

<!-- snippet: sample_using_strict_ordering_for_control_queue -->
<a id='snippet-sample_using_strict_ordering_for_control_queue'></a>
```cs
var builder = Host.CreateApplicationBuilder()
    .UseWolverine(opts =>
    {
        opts.UseRabbitMq();

        // Wolverine will *only* listen to this queue
        // on one single node and process messages in strict
        // order
        opts.ListenToRabbitQueue("control").ListenWithStrictOrdering();

        opts.Publish(x =>
        {
            // Just keying off a made up marker interface
            x.MessagesImplementing<IControlMessage>();
            x.ToRabbitQueue("control");
        });
    });
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/DocumentationSamples/ConcurrencyExamples.cs#L13-L33' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_strict_ordering_for_control_queue' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The strict ordering usage definitely limits the throughput in your system while largely eliminating issues due to concurrency.
This option is useful for fast processing messages where you may be coordinating long running work throughout the rest of 
your system. This has proven useful in file ingestion processes or systems that have to manage long running processes
in other nodes.

More likely though, to both protect against concurrent access against resources that are prone to issues with concurrent access
*and* allow for greater throughput, you may want to reach for either:

* [Session Identifier and FIFO queue support for Azure Service Bus](/guide/messaging/transports/azureservicebus/session-identifiers)
* Wolverine's [Partitioned Sequential Messaging](/guide/messaging/partitioning) feature introduced in Wolverine 5.0 that was designed specifically to alleviate problems with concurrency within
  Wolverine systems.
