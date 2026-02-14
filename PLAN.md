# Plan: Sending Failure Policies (Issue #1686)

## Problem

When an outbound message is rejected by a transport/broker (e.g., Kafka message too large, Azure SB size limit), the durable sending agent retries it forever. There's no way to discard, move to DLQ, or apply exception-specific policies to **sending** failures. The existing error handling model only applies to **handler** (incoming message) failures.

Additionally, Azure Service Bus `TryAddMessage` silently fails for oversized messages, causing message loss without any error.

## Design Decisions (from clarification)

- **Error queue**: Default to `wolverine_dead_letters` table; allow transports to override with native DLQ
- **Policy scope**: Global defaults on `WolverineOptions`, with per-endpoint overrides
- **Attempt tracking**: New `Envelope.SendAttempts` counter (separate from handler `Attempts`)
- **Batch failures**: Apply matched continuation to entire failed batch
- **Lifecycle**: Extend existing `IEnvelopeLifecycle` so existing `IContinuation` implementations can be reused
- **Latch sender**: Standalone + composable (like `PauseListenerContinuation`)
- **Raise message**: Base on existing `UserDefinedContinuation`/`CustomAction` pattern
- **Oversized detection**: Include detection of messages that can never fit in a batch

## Implementation Steps

### Step 1: Add `Envelope.SendAttempts` property

**File**: `src/Wolverine/Envelope.cs`

Add a new `int SendAttempts` property to `Envelope`. This tracks how many times a send has been attempted for this envelope, independently from handler `Attempts`. Increment it in `SendingAgent` before evaluating failure policies.

### Step 2: Create `SendingFailurePolicy` infrastructure

**New file**: `src/Wolverine/ErrorHandling/SendingFailurePolicies.cs`

Create a `SendingFailurePolicies` class that:
- Implements `IWithFailurePolicies` (exposes `FailureRuleCollection Failures`)
- This lets us reuse the entire existing `PolicyExpression`, `FailureRule`, `FailureSlot`, `IContinuationSource`, and `IContinuation` infrastructure via the existing `ErrorHandlingPolicyExtensions.OnException<T>()` extension methods
- Has a `DetermineAction(Exception, Envelope)` method that delegates to `FailureRuleCollection.DetermineExecutionContinuation()` but returns `null` if no rules match (unlike handler failures, the default for unmatched sending exceptions should be the existing retry behavior, NOT `MoveToErrorQueue`)

Key difference from handler failure policies: **no default fallback**. If no rule matches, return `null` so `SendingAgent` falls through to its existing retry/circuit-breaker behavior. This is critical for backwards compatibility.

### Step 3: Create `SendingEnvelopeLifecycle` adapter

**New file**: `src/Wolverine/Transports/Sending/SendingEnvelopeLifecycle.cs`

Create a class that implements `IEnvelopeLifecycle` with sending-appropriate behavior:

- **`Envelope`**: The outgoing envelope being sent
- **`CompleteAsync()`** → Delete from outbox (durable) or discard from in-memory queue (buffered). This is the "discard" path.
- **`MoveToDeadLetterQueueAsync(Exception)`** → Store in `wolverine_dead_letters` table via `IMessageStore.Inbox.MoveToDeadLetterStorageAsync()`, then delete from outbox. Allow transports to override if they have native DLQ support.
- **`DeferAsync()`** → Re-enqueue for retry (existing behavior)
- **`RetryExecutionNowAsync()`** → Re-post to `_sending` RetryBlock for immediate retry
- **`ReScheduleAsync(DateTimeOffset)`** → Mark as scheduled with the given time in the outbox
- **`IMessageBus` methods** → Delegate to a `MessageContext` created from the runtime, enabling "raise a message" via `PublishAsync()` inside custom actions
- **`SendAcknowledgementAsync()`/`SendFailureAcknowledgementAsync()`/`RespondToSenderAsync()`** → No-op or throw `NotSupportedException` (doesn't apply to outgoing)
- **`FlushOutgoingMessagesAsync()`** → Delegate to inner `MessageContext`

Constructor takes: `Envelope envelope`, `IWolverineRuntime runtime`, `ISendingAgent agent`, `IMessageOutbox? outbox` (null for non-durable).

### Step 4: Add `SendingFailurePolicies` to `WolverineOptions`

**File**: `src/Wolverine/WolverineOptions.cs`

Add a public property:
```csharp
public SendingFailurePolicies SendingFailure { get; } = new();
```

This enables the user-facing API:
```csharp
opts.SendingFailure.OnException<ProduceException<string, byte[]>>(
    e => e.Message.Contains("Message size too large")).Discard();
```

### Step 5: Add per-endpoint `SendingFailurePolicies` to `Endpoint`

**File**: `src/Wolverine/Configuration/Endpoint.cs`

Add a `SendingFailurePolicies? SendingFailure` property. When set, it combines with the global policies (endpoint-specific rules take priority, similar to how `FailureRuleCollection.CombineRules()` works for handler chains).

Expose it through the endpoint fluent configuration so users can configure per-endpoint:
```csharp
opts.PublishAllMessages().ToKafkaTopic("my-topic")
    .ConfigureSending(s => s.OnException<...>().Discard());
```

### Step 6: Integrate into `SendingAgent`

**File**: `src/Wolverine/Transports/Sending/SendingAgent.cs`

Modify the constructor to accept `SendingFailurePolicies` (resolved from combining global + endpoint-specific).

Modify `MarkProcessingFailureAsync(Envelope, Exception)` and `markFailedAsync(OutgoingMessageBatch)`:

1. Before existing retry/circuit-breaker logic, increment `envelope.SendAttempts`
2. Consult `SendingFailurePolicies.DetermineAction(exception, envelope)`
3. If a continuation is returned:
   - Create `SendingEnvelopeLifecycle` for each affected envelope
   - Execute the continuation via `continuation.ExecuteAsync(lifecycle, runtime, now, activity)`
   - Return (skip existing retry logic)
4. If `null` (no match), fall through to existing behavior (retry → circuit breaker)

For batch failures (via `ISenderCallback`), the exception needs to be passed through. Currently `markFailedAsync(OutgoingMessageBatch)` doesn't receive the exception — only `MarkProcessingFailureAsync(OutgoingMessageBatch, Exception?)` does. Ensure the exception propagates so policies can match on it.

Add `IWolverineRuntime` to the `SendingAgent` constructor (needed for `SendingEnvelopeLifecycle`).

### Step 7: Create `PauseSendingContinuation`

**New file**: `src/Wolverine/ErrorHandling/PauseSendingContinuation.cs`

Create a continuation that latches (pauses) the sender, similar to `PauseListenerContinuation`:
- `ExecuteAsync()` calls `agent.LatchAndDrainAsync()` on the sending agent
- Works as standalone or composable via `And()`

### Step 8: Add sending-specific actions to the fluent interface

Extend the sending failure fluent API to include:
- **`PauseSending()`** / **`AndPauseSending()`** — standalone and composable latch action
- The existing actions (`Discard()`, `MoveToErrorQueue()`, `RetryNow()`, `ScheduleRetry()`, `CustomAction()`) work as-is through `IFailureActions`/`PolicyExpression` reuse

### Step 9: Integrate into `InlineSendingAgent`

**File**: `src/Wolverine/Transports/Sending/InlineSendingAgent.cs`

Add similar failure policy evaluation in `sendWithTracing` and `sendWithOutTracing`. Since `InlineSendingAgent` has no circuit breaker, the "latch sender" action won't apply, but Discard/DLQ/CustomAction should work.

### Step 10: Handle oversized messages in `BatchedSender` / transport protocols

**File**: `src/Wolverine/Transports/Sending/BatchedSender.cs` and transport-specific protocol files

When `ServiceBusMessageBatch.TryAddMessage()` returns false on an **empty** batch (meaning the message is too large for any batch), detect this and route the envelope through the sending failure policies with a new `MessageTooLargeException`:

```csharp
if (serviceBusMessageBatch.Count == 0 && !serviceBusMessageBatch.TryAddMessage(message))
{
    // This message can never fit in any batch
    throw new MessageTooLargeException(envelope, serviceBusMessageBatch.MaxSizeInBytes);
}
```

**New file**: `src/Wolverine/Transports/MessageTooLargeException.cs`

This gives users a concrete exception type to match on:
```csharp
opts.SendingFailure.OnException<MessageTooLargeException>().Discard();
```

Apply this pattern to `AzureServiceBusSenderProtocol` (both `sendBatches` and `sendPartitionedBatches`).

### Step 11: Wire up in `Endpoint.BuildAgent()`

**File**: `src/Wolverine/Configuration/Endpoint.cs` (or wherever sending agents are constructed)

When building sending agents, resolve the combined `SendingFailurePolicies` (global merged with endpoint-specific) and pass it to the `SendingAgent`/`DurableSendingAgent`/`BufferedSendingAgent` constructors.

## Files to Create

| File | Purpose |
|------|---------|
| `src/Wolverine/ErrorHandling/SendingFailurePolicies.cs` | Policy collection + `IWithFailurePolicies` for sending |
| `src/Wolverine/Transports/Sending/SendingEnvelopeLifecycle.cs` | `IEnvelopeLifecycle` adapter for outgoing envelopes |
| `src/Wolverine/ErrorHandling/PauseSendingContinuation.cs` | Continuation to pause/latch a sender |
| `src/Wolverine/Transports/MessageTooLargeException.cs` | Exception for oversized messages |

## Files to Modify

| File | Change |
|------|--------|
| `src/Wolverine/Envelope.cs` | Add `SendAttempts` property |
| `src/Wolverine/WolverineOptions.cs` | Add `SendingFailure` property |
| `src/Wolverine/Configuration/Endpoint.cs` | Add per-endpoint `SendingFailure`, wire into agent construction |
| `src/Wolverine/Transports/Sending/SendingAgent.cs` | Integrate failure policies into `markFailedAsync` / `MarkProcessingFailureAsync` |
| `src/Wolverine/Transports/Sending/InlineSendingAgent.cs` | Integrate failure policies into send methods |
| `src/Wolverine/Persistence/Durability/DurableSendingAgent.cs` | Pass through runtime/policies to base |
| `src/Wolverine/Transports/Sending/BufferedSendingAgent.cs` | Pass through runtime/policies to base |
| `src/Wolverine/Transports/Sending/BatchedSender.cs` | Propagate exception to callback methods |
| `src/Transports/Azure/Wolverine.AzureServiceBus/Internal/AzureServiceBusSenderProtocol.cs` | Detect oversized messages via `TryAddMessage` on empty batch |

## User-Facing API Examples

```csharp
// Global: discard messages that are too large for the broker
opts.SendingFailure.OnException<MessageTooLargeException>().Discard();

// Global: Kafka-specific size error
opts.SendingFailure.OnException<ProduceException<string, byte[]>>(
    e => e.Message.Contains("Message size too large")).Discard();

// Global: move unresolvable errors to dead letter storage after 3 retries
opts.SendingFailure.OnException<ServiceBusException>()
    .RetryTimes(3).Then.MoveToErrorQueue();

// Global: custom action to publish a notification on send failure
opts.SendingFailure.OnException<ServiceBusException>()
    .CustomAction((runtime, lifecycle, ex) => {
        return lifecycle.PublishAsync(new SendingFailed(lifecycle.Envelope!.Id, ex.Message));
    }, "Notify on send failure");

// Per-endpoint override
opts.PublishAllMessages().ToAzureServiceBusTopic("orders")
    .ConfigureSending(s => s.OnException<ServiceBusException>().Discard());
```

## Backwards Compatibility

- **No breaking changes**: All existing behavior is preserved when no sending failure policies are configured
- **Default fallback**: When no rule matches, the existing retry → circuit breaker flow executes unchanged
- **Existing `ISenderCallback` contract**: Unchanged — policies are evaluated inside `SendingAgent`, transparent to transports
