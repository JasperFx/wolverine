# Sending Error Handling

Wolverine's existing [error handling](/guide/handlers/error-handling) policies apply to failures that happen while *processing* incoming messages in handlers. But what about failures that happen while *sending* outgoing messages to external transports?

When an outgoing message fails to send — maybe the message broker is temporarily unavailable, a message is too large for the transport, or a network error occurs — Wolverine's default behavior is to retry and eventually trip a circuit breaker on the sending endpoint. Starting in Wolverine 5.x, you can now configure **sending failure policies** to take fine-grained action on these outgoing send failures, using the same fluent API you already know from handler error handling.

## Why Use Sending Failure Policies?

Without sending failure policies, all send failures follow the same path: retry a few times, then trip the circuit breaker, which pauses all sending on that endpoint. This is often fine, but sometimes you need more control:

* **Oversized messages**: If a message is too large for the transport's batch size, retrying will never succeed. You want to discard or dead-letter it immediately.
* **Permanent failures**: Some exceptions indicate the message can never be delivered (e.g., invalid routing, serialization issues). Retrying wastes resources.
* **Custom notification**: You may want to publish a compensating event when a send fails.
* **Selective circuit breaking**: You may want to latch (pause) the sender only for certain exception types.

## Configuring Global Sending Failure Policies

Use `WolverineOptions.SendingFailure` to configure policies that apply to all outgoing endpoints:

```csharp
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        // Discard messages that are too large for any transport batch
        opts.SendingFailure
            .OnException<MessageTooLargeException>()
            .Discard();

        // Retry sending up to 3 times, then move to dead letter storage
        opts.SendingFailure
            .OnException<TimeoutException>()
            .RetryTimes(3).Then.MoveToErrorQueue();

        // Schedule retries with exponential backoff
        opts.SendingFailure
            .OnException<IOException>()
            .ScheduleRetry(1.Seconds(), 5.Seconds(), 30.Seconds());
    }).StartAsync();
```

::: tip
If no sending failure policy matches the exception, Wolverine falls through to the existing retry and circuit breaker behavior. Your existing applications are completely unaffected unless you explicitly configure sending failure policies.
:::

## Per-Endpoint Sending Failure Policies

You can also configure sending failure policies on a per-endpoint basis using the `ConfigureSending()` method on any subscriber configuration. Per-endpoint rules take priority over global rules:

```csharp
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        // Global default: retry 3 times then dead letter
        opts.SendingFailure
            .OnException<Exception>()
            .RetryTimes(3).Then.MoveToErrorQueue();

        // Override for a specific endpoint: just discard on any failure
        opts.PublishAllMessages().ToRabbitQueue("low-priority")
            .ConfigureSending(sending =>
            {
                sending.OnException<Exception>().Discard();
            });
    }).StartAsync();
```

## Available Actions

Sending failure policies support the same actions as handler error handling:

| Action               | Description                                                                                     |
|----------------------|-------------------------------------------------------------------------------------------------|
| Retry                | Immediately retry the send inline                                                                |
| Retry with Cooldown  | Wait a short time, then retry inline                                                             |
| Schedule Retry       | Schedule the message to be retried at a certain time                                             |
| Discard              | Log and discard the message without further send attempts                                        |
| Move to Error Queue  | Move the message to dead letter storage                                                          |
| Latch Sender         | Pause the sending agent (similar to pausing a listener)                                          |
| Custom Action        | Execute arbitrary logic, including publishing compensating messages                               |

## Oversized Message Detection

Wolverine can detect messages that are too large to ever fit in a transport batch. When a message fails to be added to an *empty* batch (meaning even a single message exceeds the maximum batch size), Wolverine throws a `MessageTooLargeException`. You can handle this with a sending failure policy:

```csharp
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        // Immediately discard messages that are too large for the broker
        opts.SendingFailure
            .OnException<MessageTooLargeException>()
            .Discard();
    }).StartAsync();
```

This is currently supported for the Azure Service Bus transport, and will be extended to other transports over time.

## Latching the Sender

Similar to pausing a listener, you can latch (pause) the sending agent when a certain failure condition is detected:

```csharp
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        // On a catastrophic broker failure, latch the sender
        opts.SendingFailure
            .OnException<BrokerUnreachableException>()
            .LatchSender();

        // Or combine with another action
        opts.SendingFailure
            .OnException<BrokerUnreachableException>()
            .MoveToErrorQueue().AndLatchSender();
    }).StartAsync();
```

## Custom Actions

You can define custom logic to execute when a send failure occurs. This is useful for publishing compensating events or logging to external systems:

```csharp
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.SendingFailure
            .OnException<Exception>()
            .CustomAction(async (runtime, lifecycle, ex) =>
            {
                // Publish a notification about the send failure
                await lifecycle.PublishAsync(new SendingFailed(
                    lifecycle.Envelope!.Id,
                    ex.Message
                ));
            }, "Notify on send failure");
    }).StartAsync();
```

## Send Attempts Tracking

Wolverine tracks sending attempts separately from handler processing attempts through the `Envelope.SendAttempts` property. This counter is incremented each time a sending failure policy is evaluated, and is used internally by the failure rule infrastructure to determine which action slot to execute (e.g., retry twice, then move to error queue on the third failure).

## How It Works

Sending failure policies are evaluated *before* the existing circuit breaker logic in the sending agent. The evaluation flow is:

1. An outgoing message fails to send, producing an exception
2. `Envelope.SendAttempts` is incremented
3. Sending failure policies are evaluated against the exception and envelope
4. If a matching policy is found, its continuation is executed (discard, dead letter, retry, custom action, etc.)
5. If no policy matches, the existing retry/circuit breaker behavior proceeds as before

This means sending failure policies are purely additive — they only change behavior when explicitly configured and when a rule matches.
