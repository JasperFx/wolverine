# Error Handling

@[youtube](k5WdzL85kGs)

It's an imperfect world and almost inevitable that your Wolverine message handlers will occasionally throw exceptions as message handling fails.
Maybe because a piece of infrastructure is down, maybe you get transient network issues, or maybe a database is overloaded.

Wolverine comes with two flavors of error handling (so far). First, you can define error handling policies on message failures with fine-grained
control over how various exceptions on different message. In addition, Wolverine supports a per-endpoint [circuit breaker](https://martinfowler.com/bliki/CircuitBreaker.html) approach that will temporarily
pause message processing on a single listening endpoint in the case of a high rate of failures at that endpoint.

## Error Handling Rules

::: warning
When using `IMessageBus.InvokeAsync()` to execute a message inline, only the "Retry" and "Retry With Cooldown" error policies
are applied to the execution **automatically**. In other words, Wolverine will attempt to use retries inside the call to `InvokeAsync()` as
configured. Custom actions can be explicitly enabled for execution inside of `InvokeAsync()` as shown in a section below.
:::

Error handling rules in Wolverine are defined by three things:

1. The scope of the rule. Really just per message type or global at this point.
2. Exception matching
3. One or more actions (retry the message? discard it? move it to an error queue?)

## What to do on an error?

| Action               | Description                                                                                                                              |
|----------------------|------------------------------------------------------------------------------------------------------------------------------------------|
| Retry                | Immediately retry the message *inline* without any pause                                                                                 |
| Retry with Cooldown  | Wait a short amount of time, then retry the message inline                                                                               |
| Requeue              | Put the message at the back of the line for the receiving endpoint                                                                       |
| Schedule Retry       | Schedule the message to be retried at a certain time                                                                                     |
| Discard              | Log, but otherwise discard the message and do not attempt to execute again                                                               |
| Move to Error Queue  | Move the message to a dedicated [dead letter queue](https://en.wikipedia.org/wiki/Dead_letter_queue) and do not attempt to execute again |
| Pause the Listener   | Stop all message processing on the current listener for a set duration of time                                                           |

While we think the options above will suffice for most scenarios, it's possible to create your own action through Wolverine's `IContinuation` interface.

So what to do in any particular scenario? Here's some initial guidance:

* If the exception is a common, transient error like timeout conditions or database connectivity errors, build in a limited set of retries and potentially
  use [exponential backoff](https://en.wikipedia.org/wiki/Exponential_backoff) to avoid overloading your system (sample of this below)
* If the exception tells you that the message is invalid or could never be processed, discard the message
* If an exception happens on multiple attempts, move the message to a "dead letter queue" where it might be possible to replay at some later time
* If an exception tells you than the system or part of the system itself is completely down, you may opt to pause the message listening altogether


## Moving Messages to an Error Queue

::: tip
The actual mechanics of the error or "dead letter queue" vary between messaging transport
:::

By default, a message will be moved to an error queue when it exhausts all its configured retry/requeue slots dependent upon
the exception filter. You can, however explicitly short circuit the retries and immediately send a message to the error queue like so:

<!-- snippet: sample_send_to_error_queue -->
<a id='snippet-sample_send_to_error_queue'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        // Don't retry, immediately send to the error queue
        opts.OnException<TimeoutException>().MoveToErrorQueue();
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/Runtime/Samples/error_handling.cs#L103-L111' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_send_to_error_queue' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->


## Discarding Messages

If you can detect that an exception means that the message is invalid in your system and could never be processed, just tell Wolverine to discard it:

<!-- snippet: sample_discard_when_message_is_invalid -->
<a id='snippet-sample_discard_when_message_is_invalid'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        // Bad message, get this thing out of here!
        opts.OnException<InvalidMessageYouWillNeverBeAbleToProcessException>()
            .Discard();
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/Runtime/Samples/error_handling.cs#L131-L140' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_discard_when_message_is_invalid' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

You have to explicitly discard a message or it will eventually be sent to a dead letter queue when the message has exhausted its configured retries or requeues.


## Exponential Backoff

::: tip
This error handling strategy is effective for slowing down or throttling processing to give a distressed subsystem a chance to recover
:::

Exponential backoff error handling is easy with either the `RetryWithCooldown()` syntax shown below:

<!-- snippet: sample_exponential_backoff -->
<a id='snippet-sample_exponential_backoff'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        // Retry the message again, but wait for the specified time
        // The message will be dead lettered if it exhausts the delay
        // attempts
        opts
            .OnException<SqlException>()
            .RetryWithCooldown(50.Milliseconds(), 100.Milliseconds(), 250.Milliseconds());
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/Runtime/Samples/error_handling.cs#L145-L157' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_exponential_backoff' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Or through attributes on a single message:

<!-- snippet: sample_exponential_backoff_with_attributes -->
<a id='snippet-sample_exponential_backoff_with_attributes'></a>
```cs
[RetryNow(typeof(SqlException), 50, 100, 250)]
public class MessageWithBackoff
{
    // whatever members
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/Runtime/Samples/error_handling.cs#L181-L188' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_exponential_backoff_with_attributes' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->


## Jitter

When many nodes retry at the same fixed delay after a shared downstream failure, they produce a "thundering herd" that pounds the recovering dependency in lockstep. Wolverine supports **additive jitter** on the three delay-based error policies: `RetryWithCooldown`, `ScheduleRetry` / `ScheduleRetryIndefinitely`, and `PauseThenRequeue`.

**Invariant:** jitter only *extends* the configured delay, never shortens it. The configured values remain the lower bound.

Three strategies are available. They are mutually exclusive per error rule.

Jitter is applied once per error rule — all slots in the rule share the same strategy, including those added via `.Then`. Attempting to call a second `WithXxxJitter()` method on the same rule (even after `.Then`) throws `InvalidOperationException`.

### WithFullJitter

Effective delay ∈ `[d, 2·d]`.

```csharp
opts.OnException<DownstreamUnavailableException>()
    .RetryWithCooldown(50.Milliseconds(), 100.Milliseconds(), 250.Milliseconds())
    .WithFullJitter();
```

### WithBoundedJitter

Effective delay ∈ `[d, d × (1 + percent)]`. Useful when you deliberately picked the cooldown values and want to keep the spread narrow.

```csharp
opts.OnException<DownstreamUnavailableException>()
    .ScheduleRetry(1.Seconds(), 5.Seconds(), 30.Seconds())
    .WithBoundedJitter(0.25); // +0% to +25%
```

`percent` must be greater than zero; there is no upper bound.

### WithExponentialJitter

Effective delay ∈ `[d, d × (1 + 2·attempt)]`. The spread widens with every attempt, so persistent failures fan out more than transient ones. This is an attempt-scaled, stateless variant of the "decorrelated jitter" pattern — it deliberately avoids persisting the previous actual delay per envelope.

```csharp
opts.OnException<DownstreamUnavailableException>()
    .PauseThenRequeue(5.Seconds())
    .WithExponentialJitter();
```


## Pausing Listening on Error Conditions

::: tip
This feature exists in Wolverine because of the exact scenario described as an example in this section. Wish we'd had Wolverine then...
:::

A common usage of asynchronous messaging frameworks is to make calls to an external API as a discrete step within a discrete message handler to isolate the
calls to that external API from the rest of your application and put those calls into its own, isolated retry loop in the case of failures. Great! But what if something happens
to that external API such that it's completely unable to accept any requests without manual intervention? You don't want to keep retrying messages that will just fail and
eventually land in a dead letter queue where they can't be easily retried without manual intervention.

Instead, let's just tell Wolverine to immediately pause all message processing in the incoming message listener when a certain exception is detected like so:

<!-- snippet: sample_pause_when_system_is_unusable -->
<a id='snippet-sample_pause_when_system_is_unusable'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        // The failing message is requeued for later processing, then
        // the specific listener is paused for 10 minutes
        opts.OnException<SystemIsCompletelyUnusableException>()
            .Requeue().AndPauseProcessing(10.Minutes());
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/Runtime/Samples/error_handling.cs#L116-L126' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_pause_when_system_is_unusable' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->


## Scoping

::: tip
To be clear, the error rules are "fall through," meaning that the rules are evaluated in order.
:::

In order of precedence, exception handling rules can be defined at either the specific message type or
globally. As a third possibility, you can use a chain policy to specify exception handling rules with any kind of user defined logic -- usually against a subset of message types.

::: tip
The Wolverine team recommends using one style (attributes or fluent interface) or another, but not to mix and match styles too much
within the same application so as to make reasoning about the error handling too difficult.
:::

First off, you can define error handling rules for a specific message type by placing attributes on either the
handler method or the message type itself as shown below:

<!-- snippet: sample_configuring_error_handling_with_attributes -->
<a id='snippet-sample_configuring_error_handling_with_attributes'></a>
```cs
public class AttributeUsingHandler
{
    [ScheduleRetry(typeof(IOException), 5)]
    [RetryNow(typeof(SqlException), 50, 100, 250)]
    [RequeueOn(typeof(InvalidOperationException))]
    [MoveToErrorQueueOn(typeof(DivideByZeroException))]
    [MaximumAttempts(2)]
    public void Handle(InvoiceCreated created)
    {
        // handle the invoice created message
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/Runtime/Samples/error_handling.cs#L242-L256' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_configuring_error_handling_with_attributes' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

You can also use the fluent interface approach on a specific message type if you put a method with the signature `public static void Configure(HandlerChain chain)`
on the handler class itself as in this sample:

<!-- snippet: sample_configure_error_handling_per_chain_with_configure -->
<a id='snippet-sample_configure_error_handling_per_chain_with_configure'></a>
```cs
public class MyErrorCausingHandler
{
    // This method signature is meaningful
    public static void Configure(HandlerChain chain)
    {
        // Requeue on IOException for a maximum
        // of 3 attempts
        chain.OnException<IOException>()
            .Requeue();
    }

    public void Handle(InvoiceCreated created)
    {
        // handle the invoice created message
    }

    public void Handle(InvoiceApproved approved)
    {
        // handle the invoice approved message
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/Runtime/Samples/error_handling.cs#L208-L231' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_configure_error_handling_per_chain_with_configure' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

To specify global error handling rules, use the fluent interface directly on `WolverineOptions.Handlers` as shown below:

<!-- snippet: sample_globalerrorhandlingconfiguration -->
<a id='snippet-sample_globalerrorhandlingconfiguration'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.Policies.OnException<TimeoutException>().ScheduleRetry(5.Seconds());
        opts.Policies.OnException<SecurityException>().MoveToErrorQueue();

        // You can also apply an additional filter on the
        // exception type for finer grained policies
        opts.Policies
            .OnException<SocketException>(ex => ex.Message.Contains("not responding"))
            .ScheduleRetry(5.Seconds());
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/Runtime/Samples/error_handling.cs#L30-L44' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_globalerrorhandlingconfiguration' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

TODO -- link to chain policies, after that exists:)

Lastly, you can use chain policies to add error handling policies to a selected subset of message handlers. First, here's
a sample policy that applies an error handling policy based on `SqlException` errors for all message types from a certain namespace:

<!-- snippet: sample_errorhandlingpolicy -->
<a id='snippet-sample_errorhandlingpolicy'></a>
```cs
// This error policy will apply to all message types in the namespace
// 'MyApp.Messages', and add a "requeue on SqlException" to all of these
// message handlers
public class ErrorHandlingPolicy : IHandlerPolicy
{
    public void Apply(IReadOnlyList<HandlerChain> chains, GenerationRules rules, IServiceContainer container)
    {
        var matchingChains = chains
            .Where(x => x.MessageType.IsInNamespace("MyApp.Messages"));

        foreach (var chain in matchingChains) chain.OnException<SqlException>().Requeue(2);
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/Runtime/Samples/error_handling.cs#L191-L206' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_errorhandlingpolicy' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Exception Filtering

::: tip
While many of the examples in this page have shown simple policies based on the type `SqlException`, in real life
you would probably want to filter on specific error codes to fine tune your error handling for SQL failures that
are transient versus failures that imply the message could never be processed.
:::

The attributes are limited to exception type, but the fluent interface has quite a few options to filter exception further with additional
filters, inner exception tests, and compound filters:

sample_filtering_by_exception_type


## Custom Actions

::: tip
For the sake of granular error handling, it's recommended that your
custom error handler code limit itself to publishing additional messages
rather than trying to do work inline
:::

Wolverine will enable you to create custom exception handling actions as additional steps to take during message failures.
As an example, let's say that when your system is sent a `ShipOrder` message you'd like to send the original
sending service a corresponding `ShippingFailed` message when that `ShipOrder` message fails during processing.

The following code shows how to do this with an inline function:

<!-- snippet: sample_inline_exception_handling_action -->
<a id='snippet-sample_inline_exception_handling_action'></a>
```cs
theReceiver = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.ListenAtPort(receiverPort);
        opts.ServiceName = "Receiver";

        opts.Policies.OnException<ShippingFailedException>()
            .Discard().And(async (_, context, _) =>
            {
                if (context.Envelope?.Message is ShipOrder cmd)
                {
                    await context.RespondToSenderAsync(new ShippingFailed(cmd.OrderId));
                }
            });
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/ErrorHandling/custom_error_action_raises_new_message.cs#L31-L48' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_inline_exception_handling_action' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Optionally, you can implement a new type to handle this same custom logic by
subclassing the `Wolverine.ErrorHandling.UserDefinedContinuation` type like so:

<!-- snippet: sample_shippingorderfailurepolicy -->
<a id='snippet-sample_shippingorderfailurepolicy'></a>
```cs
public class ShippingOrderFailurePolicy : UserDefinedContinuation
{
    public ShippingOrderFailurePolicy() : base(
        $"Send a {nameof(ShippingFailed)} back to the sender on shipping order failures")
    {
    }

    public override async ValueTask ExecuteAsync(IEnvelopeLifecycle lifecycle, IWolverineRuntime runtime,
        DateTimeOffset now, Activity? activity)
    {
        if (lifecycle.Envelope?.Message is ShipOrder cmd)
        {
            await lifecycle
                .RespondToSenderAsync(new ShippingFailed(cmd.OrderId));
        }
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/ErrorHandling/custom_error_action_raises_new_message.cs#L76-L95' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_shippingorderfailurepolicy' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

and register that secondary action like this:

<!-- snippet: sample_registering_custom_user_continuation_policy -->
<a id='snippet-sample_registering_custom_user_continuation_policy'></a>
```cs
theReceiver = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.ListenAtPort(receiverPort);
        opts.ServiceName = "Receiver";

        opts.Policies.OnException<ShippingFailedException>()
            .Discard().And<ShippingOrderFailurePolicy>();
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/ErrorHandling/custom_error_action_raises_new_message.cs#L115-L126' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_registering_custom_user_continuation_policy' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Circuit Breaker

::: tip
At this point, the circuit breaker mechanics need to be applied on an endpoint by endpoint basis
:::

Wolverine also supports a [circuit breaker](https://martinfowler.com/bliki/CircuitBreaker.html)
strategy for handling errors. The purpose of a circuit breaker is to pause message handling
*for a single endpoint* if there are a significant percentage of message failures
in order to allow the system to catch up and possibly allow for a distressed subsystem
to recover and stabilize.

The usage of the Wolverine circuit breaker is shown below:

<!-- snippet: sample_circuit_breaker_usage -->
<a id='snippet-sample_circuit_breaker_usage'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.Policies.OnException<InvalidOperationException>()
            .Discard();

        opts.ListenToRabbitQueue("incoming")
            .CircuitBreaker(cb =>
            {
                // Minimum number of messages encountered within the tracking period
                // before the circuit breaker will be evaluated
                cb.MinimumThreshold = 10;

                // The time to pause the message processing before trying to restart
                cb.PauseTime = 1.Minutes();

                // The tracking period for the evaluation. Statistics tracking
                cb.TrackingPeriod = 5.Minutes();

                // If the failure percentage is higher than this number, trip
                // the circuit and stop processing
                cb.FailurePercentageThreshold = 10;

                // Optional allow list
                cb.Include<NpgsqlException>(e => e.Message.Contains("Failure"));
                cb.Include<SocketException>();

                // Optional ignore list
                cb.Exclude<InvalidOperationException>();
            });
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/RabbitMQ/CircuitBreakingTests/Samples.cs#L16-L49' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_circuit_breaker_usage' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Note that the exception includes and excludes are optional. If there are no explicit `Include()`
calls, the circuit breaker will assume that every exception should be considered a failure.
Likewise, if there are no `Exclude()` calls, the circuit breaker will not throw out any
exceptions. Also note that **it probably makes no sense to define both `Include()` and `Exclude()`
rules**.

## Custom Actions for InvokeAsync() <Badge type="tip" text="3.13" />

::: info
This usage was built for a [JasperFx Software](https://jasperfx.net) customer who is using Wolverine by calling `IMessageBus.InvokeAsync()`
directly underneath [Hot Chocolate mutations](https://chillicream.com/docs/hotchocolate/v13/defining-a-schema/mutations). In their case, if the 
mutation action failed more than X number of times, they wanted to send a different message that would try to jumpstart the long running
workflow that is somehow stalled.
:::

This is maybe a little specialized, but let's say you have a reason for calling `IMessageBus.InvokeAsync()` inline, and
that you want to carry out some kind of custom action if the message handler exceeds a certain number of retries (the only
error handling action that applies automatically to `InvokeAsync()`). You can now opt custom actions into applying to 
exceptions thrown by your message handlers during a call to `InvokeAsync()` by specifying an `InvokeResult` value of `Stop`
or `TryAgain` to a custom action. Here's a sample that uses a `CompensatingAction()` helper method for raising other messages
on failures:

<!-- snippet: sample_using_custom_actions_for_inline_processing -->
<a id='snippet-sample_using_custom_actions_for_inline_processing'></a>
```cs
public record ApproveInvoice(string InvoiceId);
public record RequireIntervention(string InvoiceId);

public static class InvoiceHandler
{
    public static void Configure(HandlerChain chain)
    {
        chain.OnAnyException().RetryTimes(3)
            .Then
            .CompensatingAction<ApproveInvoice>((message, ex, bus) => bus.PublishAsync(new RequireIntervention(message.InvoiceId)), 
                
                // By specifying a value here for InvokeResult, I'm making
                // this action apply to failures inside of IMessageBus.InvokeAsync()
                InvokeResult.Stop);
            
        // This is just a long hand way of doing the same thing as CompensatingAction
        // .CustomAction(async (runtime, lifecycle, _) =>
        // {
        //     if (lifecycle.Envelope.Message is ApproveInvoice message)
        //     {
        //         var bus = new MessageBus(runtime);
        //         await bus.PublishAsync(new RequireIntervention(message.InvoiceId));
        //     }
        //
        // }, "Send a compensating action", InvokeResult.Stop);
    }
    
    public static int SucceedOnAttempt = 0;
    
    public static void Handle(ApproveInvoice invoice, Envelope envelope)
    {
        if (envelope.Attempts >= SucceedOnAttempt) return;

        throw new Exception();
    }

    public static void Handle(RequireIntervention message)
    {
        Debug.WriteLine($"Got: {message}");
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/ErrorHandling/custom_action_for_inline_messages.cs#L48-L91' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_custom_actions_for_inline_processing' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

### Running custom actions indefinitely

In some scenarios you want your custom action to control the retry lifecycle across multiple attempts (e.g., reschedule with a delay until some external condition is met), instead of Wolverine moving the message to the error queue after the first attempt. For that, use `CustomActionIndefinitely(...)`.

`CustomActionIndefinitely` keeps invoking your custom action on subsequent attempts until your code explicitly stops the process. Inside the delegate you can for example:
- Reschedule the message (e.g., with backoff, or by some dynamic values based on exception's payload....) via `lifecycle.ReScheduleAsync(...)`
- Requeue if appropriate
- Or stop further processing by calling `lifecycle.CompleteAsync()` (optionally after logging or publishing a compensating message)

Example:

```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.Policies
            .OnException<SpecialException>()
            .CustomActionIndefinitely(async (runtime, lifecycle, ex) =>
            {
                // Stop after 10 attempts
                if (lifecycle.Envelope.Attempts >= 10)
                {
                    // Decide to stop trying; you could also move to an error queue
                    await lifecycle.CompleteAsync();
                    return;
                }

                // Keep trying later with a delay
                await lifecycle.ReScheduleAsync(DateTimeOffset.UtcNow.AddSeconds(15));
            }, "Handle SpecialException with conditional reschedule/stop");
    }).StartAsync();
```



Note that custom actions would *always* be applied to exceptions thrown in asynchronous message handling. 


## Fault Events

Wolverine ships an opt-in mechanism that auto-publishes a strongly-typed
`Fault<T>` envelope whenever a handler for `T` terminally fails. Use it
when distributed consumers want to react to failures programmatically,
or when a typed projection of "what failed and why" is more useful than
inspecting a generic dead-letter queue.

Fault events sit *after* your retry / requeue / DLQ rules above — they
fire when a message has reached a terminal state per those rules, not
before.

### Quickstart

Opt-in globally:

```csharp
opts.PublishFaultEvents();
```

Subscribe with a normal Wolverine handler — no special attribute, no
opt-in registration:

```csharp
public static class OrderPlacedFaultHandler
{
    public static void Handle(Fault<OrderPlaced> fault) =>
        Console.WriteLine($"Order {fault.Message.Id} failed: {fault.Exception.Message}");
}
```

Whenever `OrderPlacedHandler` fails terminally (retries exhausted, moved
to error queue, or — if opted in — discarded), Wolverine publishes a
`Fault<OrderPlaced>` envelope through the global routing graph.

### Anatomy of `Fault<T>`

```csharp
public record Fault<T>(
    T Message,
    ExceptionInfo Exception,
    int Attempts,
    DateTimeOffset FailedAt,
    string? CorrelationId,
    Guid ConversationId,
    string? TenantId,
    string? Source,
    IReadOnlyDictionary<string, string?> Headers
) where T : class;
```

- `Message` — the original failing message, exactly as it was deserialized.
- `Exception` — `ExceptionInfo` record with `Type`, `Message`, `StackTrace`, `InnerException`. Inner-exception recursion is depth-capped at 10 to bound payload size.
- `Attempts` — how many delivery attempts the failing envelope went through before the terminal decision.
- `FailedAt` — the timestamp of the terminal decision (UTC).
- `CorrelationId`, `ConversationId`, `TenantId`, `Source` — propagated from the failing envelope. **`Source` is the original sender's identity, not the fault publisher's.**
- `Headers` — copied from the failing envelope with `wolverine.encryption.*` headers stripped (encryption is decided fresh on the outbound fault hop).

The static `FaultHeaders` class exposes three constants set on every
auto-published fault envelope:

```csharp
public static class FaultHeaders
{
    public const string AutoPublished  = "wolverine.fault.auto";
    public const string OriginalId     = "wolverine.fault.original_id";
    public const string OriginalType   = "wolverine.fault.original_type";
}
```

`AutoPublished` is set to `"true"` on auto-published faults only.
Hand-published faults (`bus.PublishAsync(new Fault<T>(...))`) do not
carry this header — useful for distinguishing the two in subscribers
and tests. `OriginalId` and `OriginalType` carry the failing envelope's
ID and Wolverine message-type name so trace consumers can correlate
without inspecting the fault body.

### Delivery semantics and scope

A fault is published when:

- **Moved to error queue (DLQ)** — every retry policy that ends in DLQ.
- **Discarded** — only when the failure rule was configured with `discardWithFaultPublish: true`.
- **Expired envelope** — handler entry observes the envelope past its `DeliverBy`; counts as a terminal failure.

A fault is **not** published in these bypass paths:

- **Send-side failures** — the broker rejects the outbound publish before it ever reached a handler.
- **Unknown message type at the receiver** — Wolverine cannot synthesize a `T` to wrap.
- **Pre-handler crypto failures** — `EncryptionPolicyViolationException`, `EncryptionMissingHeaderException`, `EncryptionDecryptionException` short-circuit before the handler runs.
- **Fault-publish recursion** — a failing `Fault<T>` handler will not trigger a `Fault<Fault<T>>`. The recursion guard logs at Debug and emits a `wolverine.fault.recursion_suppressed` activity event.

> **Atomicity caveat.** Fault publish is **best-effort, not transactionally
> co-committed** with the DLQ insert. The receive-side outbox does not
> enrol the fault publish in the same transaction as the DLQ row. In the
> unlikely window where the DLQ commit succeeds but the fault enqueue
> throws, the fault is dropped (logged at Error and the failure counter
> is incremented). Subscribers must therefore be resilient to gaps; they
> cannot use Fault events as a strict audit log.

### Subscribing to faults

Standard handler discovery applies — write a method named `Handle` /
`HandleAsync` / `Consume` / `ConsumeAsync` taking `Fault<T>` for each
`T` you care about. Routing for the fault envelope uses the global
routing graph; persistence uses the same outbox/inbox you configured
for any other message.

A test-friendly subscriber distinguishes auto-published from
hand-published faults:

```csharp
public static class OrderPlacedFaultHandler
{
    public static void Handle(Fault<OrderPlaced> fault, Envelope envelope)
    {
        var auto = envelope.Headers.TryGetValue(FaultHeaders.AutoPublished, out var v)
            && v == "true";
        Console.WriteLine($"Order {fault.Message.Id} {(auto ? "auto-faulted" : "manually faulted")}");
    }
}
```

> **Naming convention.** Wolverine's conventional handler discovery
> requires class names ending in `Handler` or `Consumer` (or
> `[WolverineHandler]` on the class). A class named
> `OrderPlacedFaultSink` will not be discovered automatically.

### Per-type fault configuration

Override the global mode and redaction on a single message type:

```csharp
opts.Policies.ForMessagesOfType<OrderPlaced>()
    .PublishFault(includeExceptionMessage: true, includeStackTrace: false);

opts.Policies.ForMessagesOfType<HighVolumeChatter>()
    .DoNotPublishFault();
```

Override semantics:

- A per-type override is **fully specified** — Mode and redaction never partially inherit from globals. Calling `PublishFault()` with no parameters sets `includeExceptionMessage = true, includeStackTrace = true` (the parameter defaults), even if `PublishFaultEvents(includeExceptionMessage: false)` was set globally. Always pass the redaction flags explicitly when overriding.
- Calls must happen before host startup. `WolverineRuntime.StartAsync` calls `FaultPublishingPolicy.Freeze()`; later attempts to add or change overrides throw `InvalidOperationException`.

### Fault redaction

Two flags on `PublishFaultEvents` (global) and matching parameters on
`PublishFault` (per-type):

```csharp
opts.PublishFaultEvents(
    includeExceptionMessage: false,
    includeStackTrace: false);
```

What gets redacted:

- `Fault<T>.Exception.Message` → `string.Empty`
- `Fault<T>.Exception.StackTrace` → `null`
- Recurses through `InnerException` and `AggregateException.InnerExceptions`.
- `ExceptionInfo.Type` is always preserved (type names are in source code anyway).
- Headers, `Source`, correlation/conversation/tenant IDs are never redacted.

> **Note:** redaction targets `Fault<T>.Exception` only. The original
> message instance `T` carried as `Fault<T>.Message` is unchanged — that
> is what fault events are *for*. If `T` itself is sensitive, the
> per-type encryption pairing (next section) is the right tool.

### Fault encryption pairing

Calling `Policies.ForMessagesOfType<T>().Encrypt()` automatically
registers the encrypting serializer rule for `Fault<T>` and adds
`typeof(Fault<T>)` to the receive-side encryption requirement set. No
manual setup. Skipped for value-type `T` because `Fault<T>` requires
`T : class`.

See **[Message Encryption → Fault events](/guide/runtime/encryption#fault-events)**
for the byte-level mechanics, the `wolverine.encryption.*` header
strip, and the receive-side `RequireEncryption()` interaction. Note in
particular that `RequireEncryption()` is a **receive-side guard only** —
it does not constrain the outbound republish of an auto-published
`Fault<T>` triggered by failures on that listener.

### Fault observability

Three Activity events are added to the failing envelope's span:

- `wolverine.fault.published` — when a fault is enqueued for routing.
- `wolverine.fault.no_route` — when no route exists for `Fault<T>`. Tagged with `wolverine.fault.message_type`.
- `wolverine.fault.recursion_suppressed` — when the recursion guard fires.

One counter:

- `wolverine.fault.events_published` — `Counter<int>`, incremented per fault enqueued. Suppressed (recursion-guarded) faults do **not** increment this counter.

On publish failure (the `MUST NOT throw` contract): the publisher
catches, logs at Error, sets the activity status to Error, and emits a
`wolverine.fault.publish_failed` activity event.

Outbound `Fault<T>` envelopes inherit `ConversationId`, `CorrelationId`,
and `TraceParent` from the failing envelope, so distributed traces stay
connected across the failure → fault hop.

### Testing fault events with `ITrackedSession`

The tracked-session API surfaces auto-published faults so test
assertions don't have to subscribe explicitly:

```csharp
var tracked = await host.TrackActivity()
    .DoNotAssertOnExceptionsDetected()
    .SendMessageAndWaitAsync(new OrderPlaced(...));

var faults = tracked.AutoFaultsPublished.OfType<Fault<OrderPlaced>>().ToArray();
faults.ShouldHaveSingleItem();
```

Hand-published `bus.PublishAsync(new Fault<T>(...))` calls do **not**
appear in `AutoFaultsPublished` — only auto-header'd ones do. This lets
tests distinguish unintended auto-publishes from intentional manual
ones.

### Fault event pitfalls

- **`Fault<T>.ToString()` leaks `Message` plaintext.** Positional records auto-generate a `ToString` that includes every field. Logging `$"Got {fault}"` writes the wrapped `T` plaintext into your log sink. For sensitive `T`, use the encryption pairing AND avoid logging the fault directly.
- **`RequireEncryption()` does not constrain outbound faults.** Marking a listener `.RequireEncryption()` only rejects unencrypted *inbound* envelopes on that listener; it has no effect on whether a `Fault<T>` triggered by a failure on that listener is encrypted on its outbound hop. Use the per-type `Encrypt()` pairing for outbound protection.
- **Manual `bus.PublishAsync(new Fault<T>(...))` skips the auto-header.** That is by design — assertions and `ITrackedSession.AutoFaultsPublished` distinguish auto from manual. Filtering on `FaultHeaders.AutoPublished` excludes manual publishes.
- **Recursion suppression is silent in metrics.** A suppressed recursive fault does NOT increment `wolverine.fault.events_published`. Watch for the `wolverine.fault.recursion_suppressed` activity event if you suspect a Fault-handler is faulting.
- **Per-type override defaults are NOT global defaults.** `Policies.ForMessagesOfType<T>().PublishFault()` with no parameters uses the parameter defaults (`true` / `true`), independent of the global `PublishFaultEvents(...)` redaction settings. Always pass the redaction flags explicitly when overriding.

### See also

- [Message Encryption → Fault events](/guide/runtime/encryption#fault-events) — byte-level encryption interaction.
- [`FaultEventsDemo`](https://github.com/JasperFx/wolverine/tree/main/src/Samples/FaultEventsDemo) — runnable single-process sample.
