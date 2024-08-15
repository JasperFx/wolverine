# Error Handling

It's an imperfect world and almost inevitable that your Wolverine message handlers will occasionally throw exceptions as message handling fails.
Maybe because a piece of infrastructure is down, maybe you get transient network issues, or maybe a database is overloaded.

Wolverine comes with two flavors of error handling (so far). First, you can define error handling policies on message failures with fine-grained
control over how various exceptions on different message. In addition, Wolverine supports a per-endpoint [circuit breaker](https://martinfowler.com/bliki/CircuitBreaker.html) approach that will temporarily
pause message processing on a single listening endpoint in the case of a high rate of failures at that endpoint.


## Error Handling Rules

::: warning
When using `IMessageBus.InvokeAsync()` to execute a message inline, only the "Retry" and "Retry With Cooldown" error policies
are applied to the execution. In other words, Wolverine will attempt to use retries inside the call to `InvokeAsync()` as
configured.
:::

Error handling rules in Wolverine are defined by three things:

1. The scope of the rule. Really just per message type or global at this point.
2. Exception matching
3. One or more actions (retry the message? discard it? move it to an error queue?)

### What to do on an error?

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


### Moving Messages to an Error Queue

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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/Runtime/Samples/error_handling.cs#L107-L116' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_send_to_error_queue' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->


### Discarding Messages

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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/Runtime/Samples/error_handling.cs#L137-L147' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_discard_when_message_is_invalid' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

You have to explicitly discard a message or it will eventually be sent to a dead letter queue when the message has exhausted its configured retries or requeues.


### Exponential Backoff

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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/Runtime/Samples/error_handling.cs#L152-L165' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_exponential_backoff' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/Runtime/Samples/error_handling.cs#L190-L198' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_exponential_backoff_with_attributes' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->


### Pausing Listening on Error Conditions

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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/Runtime/Samples/error_handling.cs#L121-L132' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_pause_when_system_is_unusable' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->


### Scoping

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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/Runtime/Samples/error_handling.cs#L254-L269' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_configuring_error_handling_with_attributes' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/Runtime/Samples/error_handling.cs#L219-L243' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_configure_error_handling_per_chain_with_configure' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

To specify global error handling rules, use the fluent interface directly on `WolverineOptions.Handlers` as shown below:

<!-- snippet: sample_GlobalErrorHandlingConfiguration -->
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/Runtime/Samples/error_handling.cs#L31-L46' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_globalerrorhandlingconfiguration' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

TODO -- link to chain policies, after that exists:)

Lastly, you can use chain policies to add error handling policies to a selected subset of message handlers. First, here's
a sample policy that applies an error handling policy based on `SqlException` errors for all message types from a certain namespace:

<!-- snippet: sample_ErrorHandlingPolicy -->
<a id='snippet-sample_errorhandlingpolicy'></a>
```cs
// This error policy will apply to all message types in the namespace
// 'MyApp.Messages', and add a "requeue on SqlException" to all of these
// message handlers
public class ErrorHandlingPolicy : IHandlerPolicy
{
    public void Apply(IReadOnlyList<HandlerChain> chains, GenerationRules rules, IContainer container)
    {
        var matchingChains = chains
            .Where(x => x.MessageType.IsInNamespace("MyApp.Messages"));

        foreach (var chain in matchingChains) chain.OnException<SqlException>().Requeue(2);
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/Runtime/Samples/error_handling.cs#L201-L217' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_errorhandlingpolicy' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

### Exception Filtering

::: tip
While many of the examples in this page have shown simple policies based on the type `SqlException`, in real life
you would probably want to filter on specific error codes to fine tune your error handling for SQL failures that
are transient versus failures that imply the message could never be processed.
:::

The attributes are limited to exception type, but the fluent interface has quite a few options to filter exception further with additional
filters, inner exception tests, and compound filters:

sample_filtering_by_exception_type


### Custom Actions

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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/ErrorHandling/custom_error_action_raises_new_message.cs#L30-L48' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_inline_exception_handling_action' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Optionally, you can implement a new type to handle this same custom logic by
subclassing the `Wolverine.ErrorHandling.UserDefinedContinuation` type like so:

<!-- snippet: sample_ShippingOrderFailurePolicy -->
<a id='snippet-sample_shippingorderfailurepolicy'></a>
```cs
public class ShippingOrderFailurePolicy : UserDefinedContinuation
{
    public ShippingOrderFailurePolicy() : base(
        $"Send a {nameof(ShippingFailed)} back to the sender on shipping order failures")
    {
    }

    public override async ValueTask ExecuteAsync(IEnvelopeLifecycle lifecycle, IWolverineRuntime runtime,
        DateTimeOffset now, Activity activity)
    {
        if (lifecycle.Envelope?.Message is ShipOrder cmd)
        {
            await lifecycle
                .RespondToSenderAsync(new ShippingFailed(cmd.OrderId));
        }
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/ErrorHandling/custom_error_action_raises_new_message.cs#L74-L94' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_shippingorderfailurepolicy' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/ErrorHandling/custom_error_action_raises_new_message.cs#L114-L126' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_registering_custom_user_continuation_policy' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Transports/RabbitMQ/CircuitBreakingTests/Samples.cs#L16-L50' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_circuit_breaker_usage' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Note that the exception includes and excludes are optional. If there are no explicit `Include()`
calls, the circuit breaker will assume that every exception should be considered a failure.
Likewise, if there are no `Exclude()` calls, the circuit breaker will not throw out any
exceptions. Also note that **it probably makes no sense to define both `Include()` and `Exclude()`
rules**.
