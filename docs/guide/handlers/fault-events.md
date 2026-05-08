# Fault Events

Wolverine ships an opt-in mechanism that auto-publishes a strongly-typed
`Fault<T>` envelope whenever a handler for `T` terminally fails. Use it
when distributed consumers want to react to failures programmatically,
or when a typed projection of "what failed and why" is more useful than
inspecting a generic dead-letter queue.

Fault events sit *after* your retry / requeue / DLQ rules — they fire
when a message has reached a terminal state per those rules, not before.

## Quickstart

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

## Anatomy of `Fault<T>`

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

## Delivery semantics and scope

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

## Subscribing to faults

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

## Per-type configuration

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

## Redaction

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

## Encryption pairing

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

## Observability

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

## Testing with `ITrackedSession`

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

## Pitfalls

- **`Fault<T>.ToString()` leaks `Message` plaintext.** Positional records auto-generate a `ToString` that includes every field. Logging `$"Got {fault}"` writes the wrapped `T` plaintext into your log sink. For sensitive `T`, use the encryption pairing AND avoid logging the fault directly.
- **`RequireEncryption()` does not constrain outbound faults.** Marking a listener `.RequireEncryption()` only rejects unencrypted *inbound* envelopes on that listener; it has no effect on whether a `Fault<T>` triggered by a failure on that listener is encrypted on its outbound hop. Use the per-type `Encrypt()` pairing for outbound protection.
- **Manual `bus.PublishAsync(new Fault<T>(...))` skips the auto-header.** That is by design — assertions and `ITrackedSession.AutoFaultsPublished` distinguish auto from manual. Filtering on `FaultHeaders.AutoPublished` excludes manual publishes.
- **Recursion suppression is silent in metrics.** A suppressed recursive fault does NOT increment `wolverine.fault.events_published`. Watch for the `wolverine.fault.recursion_suppressed` activity event if you suspect a Fault-handler is faulting.
- **Per-type override defaults are NOT global defaults.** `Policies.ForMessagesOfType<T>().PublishFault()` with no parameters uses the parameter defaults (`true` / `true`), independent of the global `PublishFaultEvents(...)` redaction settings. Always pass the redaction flags explicitly when overriding.

## See also

- [Error Handling](/guide/handlers/error-handling) — retry, requeue, DLQ rules. Fault publish fires *after* terminal decisions made there.
- [Message Encryption → Fault events](/guide/runtime/encryption#fault-events) — byte-level encryption interaction.
- [`FaultEventsDemo`](https://github.com/JasperFx/wolverine/tree/main/src/Samples/FaultEventsDemo) — runnable single-process sample.
