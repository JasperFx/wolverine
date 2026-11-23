# Idempotency Keys

<Badge type="tip" text="6.31" />

::: tip
This is the HTTP half of Wolverine's [logical message
deduplication](/guide/durability/idempotency#logical-message-deduplication). Read that page first
for the storage, the retention window, and how to turn it off — everything here builds on it.
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

With `Durability.MessageDeduplicationMode` turned on (it is on by default since 7.0), that endpoint now:

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
// Program.cs. On by default since 7.0; set it to false, or call
// opts.RestoreV6Defaults(), to provision no wolverine_deduplicated_responses table
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
| The same request again | The first response: its status, body and `Location` (no body with [`StoreBody = false`](#leaving-the-body-out)), [marked as a replay](#marking-a-replay). The endpoint does not run |
| A different request | **422** with a `ProblemDetails` body. Send a new key for a new request |
| The same request while the first is still running | **409** with a `ProblemDetails` body. With [`ClaimInTransaction`](#claiming-inside-the-transaction), it runs too ([see below](#claiming-inside-the-transaction)) |
| No key | **400**, unless `Required = false` |

A request that ends before the endpoint's work is done — it throws, answers 400 or above, or is stopped
early by middleware, even successfully — gives the key back, so a retry runs. Once the work is done the
key is kept. The refusal codes are registered as endpoint metadata.

The key comes from the `Idempotency-Key` header unless `Source` and `Key` say otherwise, as for
[`[Deduplicated]`](#using-a-different-key). The attribute goes on endpoint methods, and cannot be combined
with `[Deduplicated]` on the same endpoint.

### Scope

The scope is required, on the attribute or from [`DeduplicateWithResponse()`](#applying-it-to-every-endpoint), and
decides whose requests share a key:

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

Forms are the exception. A client picks a new boundary whenever it builds the request again, so
the same upload sent twice is two different byte streams. A `multipart/*` body is compared by its parts
instead: each part's headers and content, in the order sent. A retry with a new boundary is the same
request; a different file, file name or field value is still a 422. A body that cannot be read as multipart,
or that has more parts than `FormOptions.ValueCountLimit`, is compared by its bytes.

An `application/x-www-form-urlencoded` body is compared by its fields, by name. If middleware read the
form before the endpoint ran, as `UseAntiforgery()` does for a token sent as a form field, the form it read is
compared the same way, along with its files. In every case the antiforgery token field
(`AntiforgeryOptions.FormFieldName`) is left out, because a browser that loads the page again is given a new
token for the same request.

### Leaving the body out

A stored response is a copy of whatever the endpoint answered, kept for the whole window. For an endpoint
that returns a one-time secret, that is a plaintext copy of the secret in the database. A newly created
API key is one example. So is anything else the server generates and returns once: an OAuth client
secret, a webhook signing secret, a temporary password, a one-time token. Set `StoreBody = false` to keep
it out:

<!-- snippet: sample_deduplicated_with_response_without_storing_the_body -->
<a id='snippet-sample_deduplicated_with_response_without_storing_the_body'></a>
```cs
// The secret is in the first response and is never stored. A repeat gets the 201 and Location, with no body
[DeduplicatedWithResponse(DeduplicationScope.User, StoreBody = false)]
[WolverinePost("/deduplicated-response/secret-result")]
public static Created<DeduplicatedSecret> PostSecretResult(DeduplicatedOrder request)
{
    var created = run(request);
    return TypedResults.Created($"/deduplicated-response/orders/{created.Id}",
        new DeduplicatedSecret(created.Id, Guid.NewGuid().ToString()));
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/Wolverine.Http.Tests/deduplicated_with_response.cs#L1020-L1030' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_deduplicated_with_response_without_storing_the_body' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The body is never serialized or written. The claim still stores the status and `Location`, so a repeat
is answered with those and no body, and the caller follows `Location` to the resource. The secret is not
shown again.

This fits an endpoint that answers with a `Location`, such as `Created`. One that sets no `Location`
answers a repeat with the bare status and the [replay header](#marking-a-replay), and nothing else.

### Applying it to every endpoint <Badge type="tip" text="7.0" />

An API that takes an `Idempotency-Key` on every POST doesn't need the attribute on each endpoint. Call
`DeduplicateWithResponse()` when you map the endpoints instead:

<!-- snippet: sample_deduplicate_with_response_on_every_post -->
<a id='snippet-sample_deduplicate_with_response_on_every_post'></a>
```cs
// Every POST endpoint that returns a resource is deduplicated on its
// Idempotency-Key header, as if it had
// [DeduplicatedWithResponse(DeduplicationScope.User | DeduplicationScope.Endpoint)]
opts.DeduplicateWithResponse(DeduplicationScope.User | DeduplicationScope.Endpoint);
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/Wolverine.Http.Tests/deduplicated_with_response_from_options.cs#L52-L59' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_deduplicate_with_response_on_every_post' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

That covers every POST endpoint. Pass a filter over the `HttpChain` to choose others, and set the rest of
the attribute's options — `Source` and `Key`, `Required`, `Window`, `ClaimInTransaction` and `StoreBody` — in
the last argument:

<!-- snippet: sample_deduplicate_with_response_with_a_filter -->
<a id='snippet-sample_deduplicate_with_response_with_a_filter'></a>
```cs
// The PUT endpoints too, with a key that is optional and a one hour window
opts.DeduplicateWithResponse(DeduplicationScope.User,
    chain => chain.HttpMethods.Contains("PUT"),
    x =>
    {
        x.Required = false;
        x.Window = 1.Hours();
    });
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/Wolverine.Http.Tests/deduplicated_with_response_from_options.cs#L61-L72' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_deduplicate_with_response_with_a_filter' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The scope is required here, as it is on the attribute.

An endpoint with its own `[DeduplicatedWithResponse]` keeps every setting it makes, and takes only the ones it
leaves unset from `DeduplicateWithResponse()`. `Source` and `Key` count as one setting. Leave the scope off the
attribute to change one setting and inherit the rest, the scope included:

<!-- snippet: sample_deduplicated_with_response_inheriting_the_scope -->
<a id='snippet-sample_deduplicated_with_response_inheriting_the_scope'></a>
```cs
// The scope, key and window come from DeduplicateWithResponse()
[DeduplicatedWithResponse(StoreBody = false)]
[WolverinePost("/dedup-options/inheriting")]
public static DeduplicatedOrderCreated Post(DeduplicatedOrder request) => new(Guid.NewGuid());
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/Wolverine.Http.Tests/deduplicated_with_response_from_options.cs#L400-L407' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_deduplicated_with_response_inheriting_the_scope' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

An attribute with no scope that no `DeduplicateWithResponse()` call covers is refused at startup. When more
than one call matches an endpoint, the first one fills in its settings.

An endpoint the attribute would refuse is skipped instead of failing startup: one that returns no resource,
one with `[Deduplicated]`, and one whose response is written as text, as a bare status code or by its own
`WriteResponse` method rather than as JSON. A custom `IResourceWriterPolicy` isn't seen here, so an endpoint
it writes is still refused when it's compiled. Leave that one out with the filter.

To leave out any other endpoint, mark it, or its class, with `[DisableDeduplicatedWithResponse]`. It gets no
refusal codes in its OpenAPI metadata either:

<!-- snippet: sample_disable_deduplicated_with_response -->
<a id='snippet-sample_disable_deduplicated_with_response'></a>
```cs
// Left out of DeduplicateWithResponse()
[DisableDeduplicatedWithResponse]
[WolverinePost("/dedup-options/opted-out")]
public static DeduplicatedOrderCreated PostOptedOut(DeduplicatedOrder request) => new(Guid.NewGuid());
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/Wolverine.Http.Tests/deduplicated_with_response_from_options.cs#L378-L385' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_disable_deduplicated_with_response' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

### Marking a replay

By default, Wolverine marks every replayed response with an `Idempotent-Replayed: true` header. The first
response, and the 409 and 422 refusals, never carry it.

A browser on another origin can read the header only if your CORS policy exposes it, for example with
`WithExposedHeaders("Idempotent-Replayed")`.

Rename the header, or turn it off, application-wide:

```csharp
app.MapWolverineEndpoints(opts =>
{
    opts.DeduplicatedReplayHeaderName = "X-Idempotent-Replay";

    // or send no header at all
    opts.MarkDeduplicatedReplays = false;
});
```

### Metrics and tracing

Each request that carries a key is counted once on the `wolverine.http.deduplication.outcomes` counter, tagged by
`http.route`, `http.request.method`, `source` (the service name) and `wolverine.deduplication.outcome`:

| Outcome | The request was |
|---|---|
| `claimed` | the first with its key: its work is done, and its claim is kept for repeats |
| `replayed` | a repeat, answered with the stored response |
| `mismatched` | a different request under a key already used, refused with 422 |
| `in_progress` | a repeat while the first is still running, or of a claim with no stored response, refused with 409 |
| `duplicate` | a repeat to a `[Deduplicated]` endpoint, refused with its duplicate status code |

A request without a key is not counted, and neither is a first request that fails and gives its claim back. The
same outcome is added to the request's span as a `wolverine.deduplication.{outcome}` event.

A `[Deduplicated]` endpoint counts on the same counter with two outcomes, `claimed` and `duplicate`. An endpoint
can have only one of the two attributes, so each request is still counted once. When the claim is written inside
the endpoint's transaction, `claimed` is counted once that transaction commits, even if something after the commit
fails. Message handlers count on a counter of their own, described in
[Logical Message Deduplication](/guide/durability/idempotency#metrics-and-tracing).

### The window

Claims last for the [deduplication window](/guide/durability/idempotency#the-deduplication-window), or
for `WindowInSeconds` on the attribute. As for `[Deduplicated]`, an expired claim is honoured until the
cleanup removes it, so a repeat can be answered a little after its window.

### What is guaranteed

By default the key is claimed and released on a connection of its own (below). With `ClaimInTransaction`,
an endpoint whose store can take the claim inside its own transaction writes it there instead.

#### Claiming inside the transaction

```csharp
[DeduplicatedWithResponse(DeduplicationScope.User, ClaimInTransaction = true)]
[WolverinePost("/orders")]
public static OrderCreated Post(CreateOrder command, IDocumentSession session)
```

Today that is an endpoint that commits through a Marten session with Wolverine's transactional middleware,
on a single-database store, with Wolverine's PostgreSQL message store in the same database. The claim and
its response are written together, as one row, in the same transaction as the endpoint's events,
documents and outgoing messages. Nothing is claimed up front: the endpoint first checks for an earlier
request's claim and answers from it.

- A rollback, or a process that dies before the commit, leaves no claim, so a retry runs.
- A commit leaves the claim answered, so a repeat is answered from it. There is no separate write that can
  fail after the work is done. The exception is a response that cannot be serialized: the claim is then
  committed unanswered, and repeats get 409 until it expires, so the work never runs twice.
- Two concurrent requests with the same key both run; neither waits for the other. The first to commit
  wins. The other's commit fails, on the claim's primary key or earlier on its own work (the same stream,
  a versioned document); its work rolls back, and it is answered from the winner's claim: the stored
  response for the same request, 422 for a different one. **Work outside the transaction runs for both**:
  a call to another service, a message sent without the outbox. Claim-and-release would have refused the
  second with 409 before it did anything. This is why the option is off by default.
- A concurrent repeat can also fail before it ever reaches its commit, because it read the first
  request's committed work: a validation that answers 409 "already open", a 404, an exception. The claim
  commits in the same transaction as that work, so the repeat looks the claim up again and is answered
  from it, as above. A failure with no claim for its key goes to the client as the endpoint answered it.
  To allow that, a request's response is held back until it commits, and only a failure before the commit
  (an answer of 400 and up, or an exception) pays the extra read.
- The losing request's response is cleared before it is answered, as ASP.NET Core's exception handler
  clears a response it replaces, so a header its rolled-back work set does not go out. That clears headers
  other middleware set directly on the response too; ones added through `OnStarting` are kept.
- A failure inside the commit call after the database commit (on Marten, the outbox flush or a session
  listener) is rethrown, as on claim-and-release, and a retry is answered from the claim.
- The response is captured just before the commit, after the endpoint has set the status and `Location`.
  A value only the commit assigns (a document version, say) is not in the stored body. An endpoint whose
  response is only produced after the commit, such as one returning `UpdatedAggregate`, keeps
  claim-and-release.

An endpoint that asks for `ClaimInTransaction` but cannot take it — another store, database per tenant, a
message store in another database, no commit through the middleware — keeps claim-and-release, and
Wolverine logs a warning naming it once its code is generated. The database check is repeated at runtime,
so pre-generated code built against a different configuration fails loudly rather than writing claims
where they are never read.

Don't call `SaveChangesAsync` yourself in such an endpoint as well. Your own commit writes the work
before the middleware's commit writes the claim, so a crash between them, or a concurrent repeat, runs
the work twice.

#### Turning it on for every endpoint

`ClaimInTransaction` is off by default. To make it the default for every `[DeduplicatedWithResponse]`
endpoint in your application, set `DefaultClaimInTransaction` when you map the endpoints:

<!-- snippet: sample_default_claim_in_transaction -->
<a id='snippet-sample_default_claim_in_transaction'></a>
```cs
// Every [DeduplicatedWithResponse] endpoint that doesn't say otherwise
// writes its claim inside its own transaction where it can
opts.DefaultClaimInTransaction = true;
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/Wolverine.Http.Tests/Marten/deduplicated_with_response_claim_in_transaction_default.cs#L56-L62' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_default_claim_in_transaction' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

An endpoint that doesn't set `ClaimInTransaction` takes that default. An endpoint that sets it, either
way, keeps its own choice:

<!-- snippet: sample_override_default_claim_in_transaction -->
<a id='snippet-sample_override_default_claim_in_transaction'></a>
```cs
// Takes WolverineHttpOptions.DefaultClaimInTransaction
[DeduplicatedWithResponse(DeduplicationScope.User)]
[WolverinePost("/claim-default/unset")]
public static DeduplicatedOrderCreated PostUnset(DeduplicatedOrder request, IDocumentSession session)
    => store(request, session);

// Keeps claim-and-release whatever the application default is
[DeduplicatedWithResponse(DeduplicationScope.User, ClaimInTransaction = false)]
[WolverinePost("/claim-default/off")]
public static DeduplicatedOrderCreated PostOff(DeduplicatedOrder request, IDocumentSession session)
    => store(request, session);
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/Wolverine.Http.Tests/Marten/deduplicated_with_response_claim_in_transaction_default.cs#L167-L181' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_override_default_claim_in_transaction' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Endpoints that can't claim inside their transaction keep claim-and-release, as above. When they only
inherited the default instead of asking for it, Wolverine logs that at debug level rather than as a
warning.

To fail instead of logging, set `ThrowWhenClaimInTransactionFallsBack`. It is off by default. When it is
on, generating the code for an endpoint that would fall back to claim-and-release throws an
`InvalidOperationException` naming the endpoint and the reason. That applies whether the endpoint set
`ClaimInTransaction = true` itself or inherited `DefaultClaimInTransaction`:

<!-- snippet: sample_throw_when_claim_in_transaction_falls_back -->
<a id='snippet-sample_throw_when_claim_in_transaction_falls_back'></a>
```cs
opts.ThrowWhenClaimInTransactionFallsBack = true;
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/Wolverine.Http.Tests/Marten/deduplicated_with_response_claim_in_transaction_default.cs#L240-L244' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_throw_when_claim_in_transaction_falls_back' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

It throws when the endpoint's code is generated: inside `MapWolverineEndpoints` when `WarmUpRoutes` is
`RouteWarmup.Eager`, and otherwise on the first request to that endpoint. Pre-generated code
(`TypeLoadMode.Static`, or `TypeLoadMode.Auto` once the generated code exists) is not generated at
runtime, so nothing throws there. Run `codegen write` with the option on to check it.

#### Claim-and-release

The key is claimed before the endpoint runs. The response is recorded on the claim after the endpoint
and any transactional commit, and before the response is written or cascaded messages flush, so a
failure after the work was done finds the claim answered and a retry is answered from it. That holds on
EF Core endpoints too: the commit and the outbox flush are separate steps, and the response is recorded
between them.

Recording the response is a write of its own, not part of the endpoint's transaction. If the process
dies between the commit and that write, repeats get 409 until the claim expires. They never run twice
within the window.

On Marten, the outbox flush and any session listeners run inside `SaveChangesAsync`, *after* the database
commit. If one of them throws, the work is already committed, so Wolverine keeps the claim even though
the response was never recorded. Repeats get 409 until the claim expires rather than running the work
again. Use [`ClaimInTransaction`](#claiming-inside-the-transaction) if you want those retries answered
with the first response instead.

Here the claim does not ride the endpoint's business transaction: a process that dies between the claim
and the endpoint finishing leaves the key claimed for the whole window (24 hours by default) rather than
releasing it with a rolled-back transaction.

### Requirements and limits

- `Durability.EnableDeduplicatedResponses` (on by default since 7.0), and the PostgreSQL, SQL Server, MySQL or SQLite message store
  (including Marten's). With a database per tenant, claims are kept in the main database. Without them
  the host logs a warning at startup and the endpoint throws at its first request.
- The endpoint must return a resource, written as JSON by System.Text.Json, or an `IResult` (below); any
  other response writer is refused when the endpoint is first compiled.
- An endpoint can also return an ASP.NET Core `IResult`. `Ok`, `Created`, `Accepted` (each with or without
  a value), `NoContent`, `StatusCodeHttpResult` and `Results.Empty` are replayed with their status, JSON
  value and `Location`, the value serialized as ASP.NET Core writes it. A `Results<...>` union is replayed
  when the arm it answers with is one of these. An arm with a status of 400 or above, or a challenge or
  forbid result, gives the key back as any failure does. Any other result, such as text, files, redirects,
  `JsonHttpResult` or `CreatedAtRoute`, is not stored: the key is kept unanswered, so repeats get 409 until
  it expires, and an error is logged. An endpoint whose declared return type can never be one of the
  replayed results is refused at startup.
- The request body is buffered to compute the fingerprint, and the resource is serialized a second
  time to store it, unless [`StoreBody = false`](#leaving-the-body-out).
- Only the status, body and `Location` are replayed; other response headers are not. The replay adds
  its own [replay header](#marking-a-replay).
- Form posts and file uploads are supported, whether the endpoint binds `[FromForm]` values, an
  `IFormFile`, an `IFormCollection` or a `MultipartReader`, and whether the antiforgery token is sent in
  the request header or as a form field.
- Responses are stored for the whole window, in a `wolverine_deduplicated_responses` table of their own,
  with their bodies unless [`StoreBody = false`](#leaving-the-body-out). The table is only provisioned when
  the setting is on, so nothing changes for anyone else; with `AutoCreate.None`, create it before turning
  the setting on.
