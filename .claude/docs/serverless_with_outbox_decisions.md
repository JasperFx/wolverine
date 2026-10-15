# `ServerlessWithOutbox`: settled design decisions (GH-5017)

These are the answers to the open questions in epic #5016 (`DurabilityMode.ServerlessWithOutbox`).
They were settled in #5017 and gate the `serverless-with-outbox` Stoat plan. Every implementing
agent on that plan should treat them as fixed. If one of them turns out to be wrong in practice,
raise it on the PR. Don't quietly change course.

## 1. "DurableInline" is applied quietly, not a public endpoint mode

"Persist in the transaction, send inline after commit, batch-delete the outbox rows" applies to
**every sending endpoint** under `ServerlessWithOutbox`. Plain `Serverless` already coerces every
endpoint to `Inline` (`ServerlessEndpointsMustBeInlinePolicy` in
`src/Wolverine/Configuration/IEndpointPolicy.cs`), and this works the same way.

- It is a real internal sending agent, **not** a new public `EndpointMode` value.
- No transport has to learn a new mode.
- It can be made public later without a breaking change.

## 2. Plain `Serverless` downgrading a durable endpoint logs one startup warning

When plain `Serverless` (not `ServerlessWithOutbox`) downgrades a `UseDurableOutbox()` endpoint to
`Inline`, Wolverine logs **one aggregated warning at startup**. The warning lists every downgraded
endpoint and points at `ServerlessWithOutbox`.

- It is a warning, not an error. Apps that use `UseDurableOutboxOnAllSendingEndpoints()` under
  `Serverless` must keep starting.
- It is one warning in total, not one per endpoint. Endpoint policies run inside
  `Endpoint.Compile()`, often lazily, so the downgraded endpoints have to be collected and reported
  together. Logging from inside the policy's `Apply()`, the way the `NativeAck` warning does, would
  give one warning per endpoint.

## 3. Default `OutboxStaleTime` under `ServerlessWithOutbox` is 60 seconds

`DurabilitySettings.OutboxStaleTime` is nullable today. Under `ServerlessWithOutbox` it defaults to
**60 seconds**. That matches Cloud Scheduler's 1-minute minimum and typical broker SDK publish
timeouts.

- A value the user sets explicitly always wins.
- The stale time is the shortest recovery delay for owner-0 rows whose inline send failed. It also
  stops a durability pass from racing the request that is still sending the row.
- You only get a duplicate if an inline send hangs for longer than 60 seconds. At-least-once
  delivery already allows that.
- Other durability modes keep their current behavior.

## 4. The outgoing `timestamp` index is opt-in

Recovery adds a new stale owner-0 predicate (`owner_id = 0 and timestamp <= @stale`). There is
**no schema change by default.**

- A new setting, proposed as `DurabilitySettings.EnableOutgoingTimestampIndex` (default `false`),
  adds an index on the outgoing table's `timestamp` column. The implementing agent may refine the
  name in its PR.
- Every RDBMS provider that can support the index gets it.
- Each document provider (RavenDB, Cosmos DB) decides whether an equivalent applies to its store.
- Each provider's gate covers the flag **both on and off**.

## 5. Push receiver mapper registration is settled in the implementing PR

How a transport registers its push-envelope mapper with the transport-agnostic Wolverine.Http
receiver is left to implementation. The agent on the `http-push-receiver` node proposes the
registration API in its PR description, and it is reviewed there.
