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

With `Durability.MessageDeduplicationMode` turned on, that endpoint now:

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

`[Deduplicated]` is **not** full Stripe-style idempotency-key support. It does not store the original
response and replay it to the second caller; it tells the second caller that the work was already
done. That is enough to make a create endpoint safe to retry, and it is considerably less machinery
than storing and versioning response bodies.

If a client genuinely needs the original response body back, it has to fetch the created resource —
which is why returning a `Location` header from the first request is worth doing.
Or use [`[DeduplicatedWithResponse]`](#answering-a-repeat-with-the-first-response), which does store it.

## Failed requests do not poison the key

If your endpoint throws, the claim is released, and a retry with the same `Idempotency-Key` gets
through. Where the endpoint carries transactional middleware the claim was written inside that
transaction and rolls back with it; otherwise Wolverine issues a compensating release.

## Who a key belongs to

A claimed key is global: it is not tied to the endpoint, the tenant or the user that claimed it. Anyone
who knows or can guess another caller's key can claim it first, and the real request is then refused as
a duplicate. With client-generated UUIDs that is impractical. With a guessable key source — a route
value, a body member, or a client that reuses simple keys — it is not, so only use one where every
caller who could present the key is trusted.

`[DeduplicatedWithResponse]` answers a repeat with a stored response, which would turn that into one
caller reading another's response, so it requires a scope.

## Answering a repeat with the first response <Badge type="tip" text="6.45" />

`[Deduplicated]` refuses a repeat, so a client whose connection dropped mid-create never learns what it
created. `[DeduplicatedWithResponse]` answers it instead:

```csharp
// Program.cs
opts.Durability.EnableDeduplicatedResponses = true;

[DeduplicatedWithResponse(DeduplicationScope.User | DeduplicationScope.Endpoint)]
[WolverinePost("/orders")]
public static OrderCreated Post(CreateOrder command, IDocumentSession session)
{
    // create the order...
}
```

For a given `Idempotency-Key`:

| Request | Answer |
|---|---|
| The first | Runs normally |
| The same request again | The first response: its status, body and `Location`. The endpoint does not run |
| A different request | **422** with a `ProblemDetails` body. Send a new key for a new request |
| The same request while the first is still running | **409** with a `ProblemDetails` body |
| No key | **400**, unless `Required = false` |

A request that ends before the endpoint's work is done — it throws, answers 400 or above, or is stopped
early by middleware, even successfully — gives the key back, so a retry runs. Once the work is done the
key is kept. The refusal codes are registered as endpoint metadata.

The key comes from the `Idempotency-Key` header unless `Source` and `Key` say otherwise, as for
[`[Deduplicated]`](#using-a-different-key). The attribute goes on endpoint methods, and cannot be combined
with `[Deduplicated]` on the same endpoint.

### Scope

The scope is required, and decides whose requests share a key:

| Flag | The key is unique within |
|---|---|
| `Tenant` | The detected tenant. Requires [tenant id detection](/guide/http/multi-tenancy) |
| `User` | `ClaimsPrincipal.Identity.Name`, else its name identifier or `sub` claim; an authenticated caller with none is refused. Anonymous callers all share one empty user |
| `Endpoint` | The HTTP method and path, route values included |

`DeduplicationScope.None` is refused at startup. Use `User`, or `Tenant | User`, unless every caller who
could present a key is trusted to see the others' responses.

Because anonymous callers all share one empty user, `User` on an endpoint with no authorization (no
`[Authorize]`, and no fallback authorization policy) scopes by nothing — exactly what refusing `None`
prevents. The host logs a warning naming the route at startup; it is only a warning because the caller
may already be authenticated by an upstream gateway.

`[AllowAnonymous]` opts an endpoint out of a fallback policy, so a `User`-scoped anonymous endpoint warns
even when one is configured. That warning names the attribute as the reason, because the fix there is to
reconsider the attribute or the scope rather than to add authorization.

### What counts as the same request

The request is compared by a SHA-256 of the bytes sent — the method and path, the query string and the
body — never by the bound message, and the key itself is always the caller's. So a retry must resend the
same bytes: a client that reorders JSON properties, changes whitespace or reserializes the body gets
a 422. A key reused on another endpoint is a different request even when the scope leaves the endpoint
out.

### The window

Claims last for the [deduplication window](/guide/durability/idempotency#the-deduplication-window), or
for `WindowInSeconds` on the attribute. As for `[Deduplicated]`, an expired claim is honoured until the
cleanup removes it, so a repeat can be answered a little after its window.

### What is guaranteed

The key is claimed before the endpoint runs. The response is recorded on the claim after the endpoint
and any transactional commit, and before the response is written or cascaded messages flush, so a
failure after the work was done finds the claim answered and a retry is answered from it. That holds on
EF Core endpoints too: the commit and the outbox flush are separate steps, and the response is recorded
between them.

Recording the response is a write of its own, not part of the endpoint's transaction. If the process
dies between the commit and that write, repeats get 409 until the claim expires. They never run twice
within the window.

Unlike `[Deduplicated]` above, the claim does not ride the endpoint's business transaction: a process
that dies between the claim and the endpoint finishing leaves the key claimed for the whole window
(24 hours by default) rather than releasing it with a rolled-back transaction.

### Requirements and limits

- `Durability.EnableDeduplicatedResponses`, and the PostgreSQL, SQL Server, MySQL or SQLite message store
  (including Marten's). With a database per tenant, claims are kept in the main database. Without them
  the host logs a warning at startup and the endpoint throws at its first request.
- The endpoint must return a resource, written as JSON by System.Text.Json; any other response writer is
  refused when the endpoint is first compiled.
- The request body is buffered to compute the fingerprint, and the resource is serialized a second
  time to store it.
- Only the status, body and `Location` are replayed; other response headers are not.
- Form-encoded requests are not supported: the fingerprint has to re-read the body after binding, and
  the form read consumes it first. An endpoint that binds a form value is refused at startup. File and
  multipart uploads are unaffected.
- Response bodies are stored for the whole window, in a `wolverine_deduplicated_responses` table of their
  own. It is only provisioned when the setting is on, so nothing changes for anyone else; with
  `AutoCreate.None`, create it before turning the setting on.
