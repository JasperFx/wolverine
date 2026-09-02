# Idempotency Keys

<Badge type="tip" text="6.31" />

::: tip
This is the HTTP half of Wolverine's [logical message
deduplication](/guide/durability/idempotency#logical-message-deduplication). Read that page first
for the storage, the retention window, and why this is opt-in — everything here builds on it.
:::

A `POST` that creates something is the classic at-most-once problem. The user double-clicks; the
client retries after a timeout it could not distinguish from a failure; a mobile app replays a queued
request when connectivity returns. Each is a separate HTTP request that means the same thing, and
without an identity for that *meaning*, all of them create a record.

Wolverine's answer is the conventional `Idempotency-Key` request header — the same header Stripe,
Adyen, and the
[IETF draft](https://datatracker.ietf.org/doc/draft-ietf-httpapi-idempotency-key-header/) already
use, so a client that already sends one gets this for free.

## Deduplicating an endpoint

```csharp
[Deduplicated]
[WolverinePost("/orders")]
public static async Task<OrderCreated> Post(CreateOrder command, IDocumentSession session)
{
    // create the order...
}
```

With `Durability.EnableMessageDeduplication` turned on, that endpoint now:

- runs normally for the first request carrying a given `Idempotency-Key`
- returns **409 Conflict** with a `ProblemDetails` body for any later request carrying the same key
  within the [deduplication window](/guide/durability/idempotency#the-deduplication-window)
- returns **400 Bad Request** with a `ProblemDetails` body for a request carrying no key at all

Both refusal codes are registered as endpoint metadata, so they appear in the generated OpenAPI
document. A 409 a client can receive but cannot discover from the spec is a contract change hidden
from exactly the people who have to handle it.

## When a replay is benign

409 is the right default — it tells the caller plainly that this request was not the one that did the
work. But some endpoints are genuinely safe to replay, and the caller would rather see success:

```csharp
// The second call gets a 204 rather than a 409
[Deduplicated(DuplicateStatusCode = 204)]
[WolverinePost("/schedules/{scheduleId}/occurrences")]
public static async Task Post(string scheduleId, ScheduleOccurrence body)
{
    // ...
}
```

Any 2xx code is written as a bare status with no body. Anything else is written as a problem
document, so a refusal always carries a machine-readable reason rather than a status code the caller
has to guess at.

## Changing the default for the whole application

`DuplicateStatusCode` on the attribute is per endpoint. When an application wants a different answer
everywhere, set it once instead:

```csharp
app.MapWolverineEndpoints(opts =>
{
    opts.DefaultDuplicateStatusCode = 422;
});
```

An endpoint that names a code still wins, **including when it names 409**. The two are told apart by
whether a code was stated at all rather than by comparing against 409, so an endpoint that deliberately
insists on 409 keeps it under an application default of something else:

```csharp
// Answers 422 -- it stated nothing, so it follows the application default
[Deduplicated]
[WolverinePost("/orders")]
public static string PostOrder(CreateOrder command) => "ok";

// Answers 409 -- it asked for 409, and meant it
[Deduplicated(DuplicateStatusCode = 409)]
[WolverinePost("/payments")]
public static string PostPayment(CapturePayment command) => "ok";
```

The application default reaches the endpoint early enough to be advertised in the OpenAPI document
too, so the spec and the runtime never disagree about which code a duplicate gets.

## Using a different key

The header name and the source are both configurable, exactly as for message handlers:

```csharp
// A different header
[Deduplicated("X-Request-Id")]

// A member of the request body
[Deduplicated(ValueSource.InputMember, nameof(CreateOrder.ClientReference))]

// A route value
[Deduplicated(ValueSource.RouteValue, "occurrenceId")]
```

## Optional keys

`Required` defaults to `true`, so an unkeyed request is a 400. Set it to `false` when some clients
send a key and some do not — the ones that do are protected, and the ones that do not are handled
exactly as if the feature were off:

```csharp
[Deduplicated(Required = false)]
[WolverinePost("/orders")]
public static async Task<OrderCreated> Post(CreateOrder command) { /* ... */ }
```

## What this is not

This is **not** full Stripe-style idempotency-key support. Wolverine does not store the original
response and replay it to the second caller; it tells the second caller that the work was already
done. That is enough to make a create endpoint safe to retry, and it is considerably less machinery
than storing and versioning response bodies.

If a client genuinely needs the original response body back, it has to fetch the created resource —
which is why returning a `Location` header from the first request is worth doing.

## Failed requests do not poison the key

If your endpoint throws, the claim is released, and a retry with the same `Idempotency-Key` gets
through. Where the endpoint carries transactional middleware the claim was written inside that
transaction and rolls back with it; otherwise Wolverine issues a compensating release.
