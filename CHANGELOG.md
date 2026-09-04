# Changelog

## Unreleased

### WolverineFx (core)

- **`FindForTenantAsync` answers for a single-database deployment.** (closes
  [#4273](https://github.com/JasperFx/wolverine/issues/4273)) The method became public API in #4268 without
  the `_onlyOneDatabase` guard that `FindAllAsync` and `FindDatabasesAsync` both open with, so on a
  single-database deployment it returned an **empty list for every tenant** rather than the store that
  actually holds their data. That is the shape of Marten *conjoined* tenancy -- one database, a `tenant_id`
  column -- because Marten only builds a `MultiTenantedMessageStore` for master-table tenancy, leaving
  `_multiTenanted` empty. The failure was silent: the method exists so a caller can query one tenant's dead
  letters without hydrating every message body, and an empty list reads as "this tenant is clean" while its
  dead letter table is full. Multi-tenanted collections still resolve through the tenancy source unchanged.


- **Concurrent callers share one tenant database list refresh.** (closes
  [#4267](https://github.com/JasperFx/wolverine/issues/4267)) `MessageStoreCollection.FindAllAsync()`
  re-enumerated a `DynamicMultiple` tenancy source on every call, and it sits on paths that are *retried on
  failure* -- listener inbox recovery, listener drain, the durability sweeps. With Marten's sharded tenancy
  that enumeration is a round trip to the tenant registry, so the retry for a connection failure opened
  another connection to look the databases up again, and concurrent callers each opened their own. On a
  512-database fleet one 26-minute process logged 233 give-ups over 176 Npgsql connect timeouts and three
  pool exhaustions, while the master database sat at 0.5% CPU with 150 of its 3600 connections in use -- it
  is the client-side data source that runs dry, and the retry then asks it for another connection.

  Concurrent callers now join a single in-flight refresh. That sharing is not configurable and is the half
  that closes the storm: the cost was the fan-out, not the frequency.

  `DurabilitySettings.TenantDatabaseListStaleTime` additionally bounds how often that one refresh happens,
  and **defaults to zero, so no existing behaviour changes**. Raise it on a large fleet. A non-zero value
  only ever affects *bulk* enumeration -- a lookup that misses (`FindDatabaseAsync`) always forces past the
  window, because it is refreshing precisely on account of not having found the database, and answering it
  from a list the window is vouching for would defeat the call. Forcing skips the freshness check, never the
  single-flight guard.


- **A node no longer sweeps up its own in-flight stop as a wedged shard.** (closes
  [#4240](https://github.com/JasperFx/wolverine/issues/4240)) An event-subscription agent could end up
  running on two nodes at once while `wolverine_nodes` credited only one of them, so nothing in the
  system could ever stop the extra copy. `StopAgentAsync` awaits the agent's own teardown before it
  deregisters the agent and drops the assignment row, and a real shard reports `Stopped` from the
  moment that teardown begins -- so for the whole duration of a stop the agent was registered,
  `Stopped`, and reporting no failure, which is exactly the state the health-check sweep treats as a
  wedged shard and restarts.

  That sweep runs on every node on every tick independently of leadership, which is why the extra
  start never appeared in the leader's own command log: the leader does not issue it, the stopping
  node issues it to itself. The restart also re-upserts the assignment row on its way through, so it
  re-claimed ownership the stop was about to revoke -- leaving one durable row and two live copies,
  a shape the GH-2602 duplicate healer compares two nodes' durable rows to find and therefore cannot
  see at all.

  The window itself is left open deliberately. The durable record has to lag in-process state while a
  shard tears down, because the row cannot be dropped before the agent has actually stopped without
  lying about ownership. Acting on the window was the defect, not the window.

### WolverineFx.RDBMS (all relational providers)

- **The advisory lock no longer shares one connection with nothing guarding it.** (closes
  [#4261](https://github.com/JasperFx/wolverine/issues/4261)) Every `AdvisoryLock` implementation --
  Postgres, SQL Server, MySQL, Oracle and SQLite -- kept one long-lived connection and synchronised
  access to it with nothing at all: no semaphore, no lock, no `Interlocked`. `HasLock` runs
  *synchronous* I/O on that connection for the GH-2602 liveness ping, while `TryAttainLockAsync`,
  `ReleaseLockAsync` and `DisposeAsync` run *asynchronous* I/O on the same field, and no ADO.NET
  provider supports concurrent use of one connection.

  The two sides are reachable together in an ordinary shutdown, which is why this is not theoretical.
  `writeHeartbeats` and `executeHealthChecks` run on the runtime-wide `Cancellation` token, which
  `shutdownAsync` only cancels *after* `teardownAgentsAsync` has returned -- and teardown "stops" those
  loops with `Task.SafeDispose()`, which disposes the `Task` object and does nothing whatsoever to the
  loop still running. A health check already past its own cancellation guard therefore reaches
  `ejectStaleNodes` -> `HasLeadershipLock()` at the same moment teardown reaches
  `NodeAgentController.StopAsync` -> `ReleaseLeadershipLockAsync`.

  It cost two different things. Where the driver *noticed* the concurrent use it threw, and `HasLock`
  reads any exception as "the server dropped our session": it cleared every held lock id and returned
  false, which `DoHealthChecksInternalAsync` reads as lost leadership and answers with `stepDownAsync`
  -- precisely the churn GH-2602 and GH-3604 were fighting. Where the driver did *not* notice, the
  protocol desynced and shutdown parked forever inside `NpgsqlConnection.CloseAsync()`, which takes no
  `CancellationToken` and so is beyond the reach of `HostOptions.ShutdownTimeout` even though that
  timeout is correctly threaded all the way down to `WolverineRuntime.StopAsync`. The reporter caught
  that one in two independent process dumps with frame-for-frame identical stacks, and its symptom is
  distinctive: every test passes and is counted, then the process never exits, because the hang lives
  in fixture disposal after the last test has already reported.

  Each implementation now serialises every touch of its connection behind a semaphore, with the held
  lock ids guarded separately so the one path that must not wait -- `HasLock`, which is synchronous and
  sits on the health-check tick -- can still read them. When that path finds the connection busy it
  reports the last state it actually established rather than `false`, and skips the ping for one tick:
  a busy connection means another advisory-lock operation is in flight *on this node*, which is no
  evidence at all that the server dropped the session, and answering `false` there is what fires the
  spurious stepdown. Every close is additionally bounded and abandoned on timeout, so a connection that
  somehow still desyncs costs seconds rather than the process.


- **Node record descriptions no longer overflow the column and fail the insert.** (closes
  [#4246](https://github.com/JasperFx/wolverine/issues/4246)) An `AssignmentChanged` record's
  description is an agent command's `ToString()`, which carries an agent URI, a schema name and a
  destination node -- long enough on a real cluster to overrun the `description` column on
  `wolverine_node_records` and fail the insert, taking the whole `AgentCommand` batch behind it down
  with it. The reporter hit it on MySQL with an overridden `Durability.MessageStorageSchemaName`:
  "Data too long for column 'description'".

  The bounded `description` columns now declare 1000 characters (`NodeRecord.DescriptionLength`) on
  MySQL, SQL Server and Oracle, and every write path clamps the description to that width first, so a
  description long enough to overflow loses its tail rather than failing an insert. These rows are
  append-only diagnostics; the tail is expendable and the assignment is not.

  MySQL was the provider that failed because it was the only one leaning on Weasel's default string
  mapping, `VARCHAR(255)` -- narrower than every other provider, and narrower than the calling code
  assumed. The rest of that node-table family had the same defect waiting in it and is widened to 500
  to match the widths SQL Server and Oracle have always declared: `wolverine_nodes.uri` and
  `.description`, `wolverine_node_assignments.id` (an agent URI as the primary key),
  `wolverine_node_records.event_name`, `wolverine_agent_restrictions.uri` and `.type`, the control
  queue's `message_type`, the dynamic listener registry's `uri`, and the tenant table's
  `connection_string`. Widths in a key stay at 500 because InnoDB caps an index key at 3072 bytes,
  which is 768 characters of utf8mb4.

  An existing database is widened in place on the next migration, by an `ALTER TABLE ... MODIFY` that
  keeps the rows -- but only with Weasel 9.30.0. Before that, the schema differ compared column types
  with the size stripped off, so a widened `varchar` was invisible to it and an existing table kept
  the narrow column forever.

### WolverineFx.Oracle

- **A sitting Oracle leader stops standing down on every tick.** (closes
  [#4275](https://github.com/JasperFx/wolverine/issues/4275)) Since the heartbeat-renewal change in
  `a84d6a262`, `NodeAgentController` calls `TryAttainLeadershipLockAsync` on every health-check tick,
  including ticks where this node is already the leader. Oracle holds its row lock in an *uncommitted*
  transaction on a dedicated connection -- that is how the lock is held -- so the renewal opened a second
  connection whose `SELECT ... FOR UPDATE NOWAIT` was blocked by the node's own first transaction and raised
  `ORA-00054`. The renewal answered `false` for a lock the node holds, and a false renewal is how the
  controller is told leadership was lost, so it called `stepDownAsync` on the very next tick after being
  elected -- and never reached `EvaluateAssignmentsAsync`, which sits on the `true` branch, so the leader's
  actual work of evaluating agent assignments never ran.

  `TryAttainLockAsync` now short-circuits on a lock this node already holds, the same way Postgres, SQL
  Server and SQLite already did. It still pings the retained connection, so a session that really did die is
  detected exactly as before (GH-2602). The existing leadership compliance suite could not have caught this:
  every one of its tests is about a *transition*, and a node that steps down re-attains immediately as the
  only candidate, leaving the end state they assert on untouched.


- **A failed attempt on the advisory lock gives its connection back.** `OracleAdvisoryLock` is the one
  implementation that opens a connection per lock rather than keeping a single shared one, and
  `TryAttainLockAsync` only released that connection on two of its exits: success, where the connection
  is retained to hold the row lock, and `ORA-00054` contention, which closes and disposes it. Every
  other failure fell through to the outer `catch`, logged, and returned false with the connection --
  and, past `BeginTransactionAsync`, an open transaction on it -- owned by nobody.

  That is not a once-per-process cost. `TryAttainLeadershipLockAsync` fires on every health-check tick,
  so a failure mode that persists rather than resolving -- the lock table's schema missing, credentials
  expired, a RAC node gone -- leaked one connection per tick until the pool was exhausted, at which
  point the node could no longer open a connection for anything else either. The regression test drives
  six attains against a pool of three: unfixed, it spends fifteen seconds queueing on an empty pool;
  fixed, it finishes in well under one.

### WolverineFx.Http

- **A Static-mode endpoint type mismatch is now reported as itself.** (closes
  [#4156](https://github.com/JasperFx/wolverine/issues/4156)) GH-4151 gave handler chains a startup assertion
  that every pre-generated type really is in `WolverineOptions.ApplicationAssembly`, because `codegen write`
  emits into the *entry* project and the two silently disagree when they are different assemblies. HTTP
  endpoint chains are a separate `ICodeFileCollection` and were left out of that pass.

  The issue expected the HTTP half to be failing lazily, on the first request to each route. It was not:
  `HttpChain.BuildEndpoint` already forces the handler build for every chain whenever `TypeLoadMode.Static`,
  independently of `RouteWarmup`, so the mapping already failed. What it failed *with* was the problem --
  JasperFx's `ExpectedTypeMissingException` on the first chain it happened to reach, naming one generated
  code file and the assembly it looked in, and nothing else. No count, no route the operator recognizes, and
  no mention of the one fact that resolves it. `HttpGraph` now asserts before it builds endpoints, so the
  whole picture arrives at once: every affected route by its route pattern, the assembly that was searched,
  and -- when the generated types are sitting in the entry assembly instead -- which assembly they are in and
  what to do about it.

- **gRPC service chains get the same assertion.** `GrpcGraph.DiscoverServices` forces nothing, so unlike HTTP
  a missing pre-built type here really did wait for the first RPC to that service, with the host reporting
  healthy the entire time. It now fails discovery, with the same diagnostic.

  Neither is a behaviour change for a correctly configured application: in `TypeLoadMode.Auto` -- the
  default -- nothing is asserted, because Auto generates what it cannot load.


- **DataAnnotations validation works with `ServiceLocationPolicy.NotAllowed`.** (closes
  [#4238](https://github.com/JasperFx/wolverine/issues/4238)) Under the Wolverine 6 default the
  application threw `InvalidServiceLocationException` at bootstrap and could not start. This was
  fallout from GH-4171: that change deliberately stopped `IServiceProvider` being answered silently
  out of `httpContext.RequestServices`, which is right for user code, but the DataAnnotations executor
  takes one to build its `ValidationContext` and so Wolverine's own middleware began tripping the
  user's policy. The validation policy now supplies that argument itself.

- **An application-wide default for the duplicate status code.**
  `opts.DefaultDuplicateStatusCode` on `MapWolverineEndpoints` sets the answer for every deduplicated
  endpoint that did not state one, so an application wanting something other than 409 says it once
  rather than on every `[Deduplicated]`. An endpoint that names a code still wins, **including when it
  names 409** -- the two are told apart by whether a code was stated rather than by comparing against
  409, so an endpoint that deliberately insists on 409 keeps it.

- **Deduplication refusals advertise their problem document in OpenAPI.** The refusal status codes
  reached the generated document, but the content type did not: `Produces` without a response type
  leaves Swashbuckle emitting the status with no content at all, so the spec announced that an
  endpoint could return a 409 while saying nothing about the `ProblemDetails` body it actually
  returns. A benign 2xx is still advertised as a bare status, deliberately -- a problem document
  describing a response the application has declared benign would be actively wrong.

### WolverineFx.DataAnnotationsValidation

- **DataAnnotations validation works with `ServiceLocationPolicy.NotAllowed` on message handlers too.**
  ([#4238](https://github.com/JasperFx/wolverine/issues/4238)) The fix above covered HTTP chains and
  stopped there. A message handler had the identical defect and kept it: `Validate<T>` takes an
  `IServiceProvider` to build its `ValidationContext`, an unsupplied one is sourced from the container
  and reported to `ServiceLocationPolicy`, and under the Wolverine 6 default of `NotAllowed` that made
  the validation middleware unusable on a handler at all. The policy now supplies
  `context.Runtime.Services` itself, for the same reasons the HTTP twin supplies
  `httpContext.RequestServices`.

  Six tests in `Wolverine.DataAnnotationsValidation.Tests` had been asserting this and failing. Nobody
  saw them, because the extension test projects ran in no CI workflow -- `TestExtensions` is reachable
  only from the `Test` and `Full` targets and no workflow invokes either. They have a lane now.

### WolverineFx.EntityFrameworkCore

- **A failed rollback no longer displaces the exception that caused it.** (closes
  [#4239](https://github.com/JasperFx/wolverine/issues/4239)) The generated `catch` for a
  `[Transactional]` handler under Wolverine-managed multi-tenancy called
  `RollbackTransactionAsync(cancellation)` unguarded, and lost the original exception two ways. With a
  token already cancelled on the way in -- which is what `DefaultExecutionTimeout` hands a nested
  `InvokeAsync` -- `BeginTransactionAsync` threw without creating a transaction and the rollback then
  threw "The connection does not have any active transactions", escaping the catch so the `throw;`
  never ran and the real failure reached neither a log nor a dead letter queue. With a token cancelled
  after the transaction opened, the rollback was handed that same token, threw, and left the
  transaction open until the `DbContext` was disposed.

  Both now go through a guarded helper: only roll back a transaction that exists, never let the token
  that caused the failure also cancel the cleanup, and never let a failure of the rollback itself
  replace the exception being unwound.

### WolverineFx.Grpc

- **Wolverine parameter attributes work on gRPC before/after hooks.** (closes
  [#3935](https://github.com/JasperFx/wolverine/issues/3935)) `[Entity]`, `[All]`, `[Queryable]`,
  `[WriteAggregate]`, `[ReadAggregate]`, `[ReadModel]`, `[FromQuerySpecification]` and the DCB
  attributes were unavailable on gRPC services -- nothing in `Wolverine.Grpc` called
  `WolverineParameterAttribute.TryApply` at all. The hooks are the only place on a gRPC service
  carrying a parameter list somebody can decorate; the RPC methods stay out on purpose, since a
  proto-defined signature leaves nowhere to hang an attribute and the answer there is the downstream
  message handler, which has supported the whole family all along.

  This also needed the before-hook applicability rule to stop rejecting them. That rule requires every
  parameter to be assignable from the RPC request type, and an `[Entity] Invoice` parameter is not --
  so the hook was dropped from codegen entirely, and the attributes would have appeared to do nothing
  while after-hooks worked fine.

### WolverineFx.AzureServiceBus

- **Wolverine's own system queues can carry an application-owned prefix, so several applications can
  share one Azure Service Bus namespace.** (related:
  [#3696](https://github.com/JasperFx/wolverine/issues/3696)) `SystemQueuePrefix("my-project")` on the
  transport configuration prepends that prefix to the four queues Wolverine names for itself:
  `my-project.wolverine.response.{ServiceName}.{node}`, `my-project.wolverine.retries.{servicename}`,
  `my-project.wolverine.control.{node}`, and `my-project.wolverine-dead-letter-queue`. Only two of
  those carry the service name today, so the control queue and the dead letter queue were shared by
  every Wolverine application pointed at the namespace -- and if you had turned on dead letter queue
  recovery, one application's recovery listener was quietly draining everybody else's dead letters
  into its own storage. Nothing changes without the opt in: with no prefix set, every one of those
  names is byte for byte what it has always been.

  I made this prepend rather than replace so the `wolverine` token survives and the queues are still
  obviously Wolverine's when you go looking in the Azure Portal. It is deliberately a separate knob
  from `PrefixIdentifiers()`, which renames your application's own queues and topics and does not
  reach the system queues -- and should not, because two cooperating applications that message each
  other have to keep addressing the same application queue names even while each one wants its own
  control and dead letter queues. Combine the two when you want everything isolated. A name you supply
  yourself is never prefixed; that is an explicit choice and Wolverine has no business second guessing
  it. The control queue is built eagerly at configuration time because the message stores read it
  before the transports initialize, so calling `SystemQueuePrefix()` after
  `EnableWolverineControlQueues()` rebuilds it under the prefixed name rather than leaving a stale
  queue behind.

- **A transport-wide default dead letter queue name.** `DefaultDeadLetterQueueName("orders-errors")`
  changes the fallback for every Azure Service Bus endpoint that does not name a dead letter queue of
  its own, instead of repeating `ConfigureDeadLetterQueue(...)` on each one. This is the Azure Service
  Bus counterpart of RabbitMQ's `CustomizeDeadLetterQueueing()` and of the SQS method of the same
  name. Resolution per endpoint is per-endpoint configuration first, then the transport default, then
  the prefixed default, then `wolverine-dead-letter-queue` -- and it resolves on read, so the order of
  those calls during bootstrapping does not matter.

### Dependencies

- JasperFx, JasperFx.Events, JasperFx.Events.SourceGenerator and JasperFx.SourceGenerator to
  **2.60.0**. `JasperFx.RuntimeCompiler` stays on its own 5.x line.
- Marten, Marten.AspNetCore and Marten.Newtonsoft to **9.30.0**, Polecat to **5.21.1**, Fisher to
  **1.0.6**, and the Weasel packages to **9.29.0**. Polecat 5.21.1 is the release that adopts the
  `IEventTenancySource` seam from JasperFx 2.59.0, which closes the async-daemon tenancy gap for a
  store that is not Marten.

### WolverineFx.AI (new package)

- **New package for one shot LLM callouts as durable messages.** (closes
  [#4227](https://github.com/JasperFx/wolverine/issues/4227)) Awaiting a language model inline in a
  handler holds a database transaction and an unacked message open for however long the model takes,
  against a service that is slow, rate limited, and sometimes down. That is the shape of work Wolverine
  already absorbs everywhere else, so `LlmCallout` makes the model call an ordinary message instead.

  `LlmCallout.Ask<IncidentTriage>(prompt, context)` returns a message you can hand back from a handler,
  an HTTP endpoint, or a projection's `RaiseSideEffects`. Returned next to a storage action it is
  enrolled in the same outbox as the write, so a callout cannot fire for a transaction that never
  committed. It runs on a dedicated durable local queue against whatever `IChatClient` the application
  registered, and the model's answer is deserialized into the requested type and published as an
  ordinary, strongly typed message with its own handler and its own retry policy. Retries, back
  pressure, scheduling, dead lettering, and the correlation chain all come along for free rather than
  being rebuilt. Turn it on with `opts.AddLlmCallouts(...)`.

  Event store integration needed nothing new, since `RaiseSideEffects` on the JasperFx.Events
  projection base already publishes messages atomically with a projection update. One integration
  therefore covers Marten, Polecat, and Fisher. Side effects stay suppressed during rebuilds, which is
  what keeps a projection rebuild from re-triaging -- and re-billing for -- two years of history.

  The package only references the **Microsoft.Extensions.AI abstractions**, never a vendor SDK, on the
  same reasoning as binding to `ILogger`. Registering the `IChatClient` stays with the application, so
  the provider and any middleware over it (`UseOpenTelemetry`, `UseDistributedCache`) remain its call.

- **Added spend guardrails as middleware on the callout queue.** `LlmBudget.MaximumPromptCharacters`
  refuses a callout before the provider is ever called, so a context accidentally assembled out of an
  unbounded collection costs nothing. `LlmBudget.MaximumTokensPerWindow` refuses callouts once a node
  has spent its allowance, counted from the usage the provider reported back. Note that the ledger is
  per process rather than cluster wide, so treat it as a circuit breaker against a runaway loop rather
  than as billing enforcement.

  Both limits dead letter rather than retry, and so does an answer that cannot be parsed into the
  requested response type. A callout that is over budget will be over budget on every attempt, and a
  prompt the model cannot answer in the requested shape gives back the same unusable answer every time,
  so retrying either one only spends more money to reach the same place. The raw model output is
  carried on `LlmCalloutException.RawResponse` so a dead letter can be triaged without a re-run. Token
  counters are published on a `Wolverine.AI` meter, tagged by the callout's `Tag` and the model that
  answered.

- **Added a scripted `IChatClient` for testing.** `StubChatClient` exercises a callout's whole round
  trip -- outbox, queue, handler, published answer -- with no key, no network, and no model. Running
  out of script is an error rather than a repeat, so a test cannot quietly hand the last answer back
  for a callout it did not know it was making. `Throw()` scripts a failure and `RespondAfter()` scripts
  a slow answer for exercising the timeout. A handler that returns a callout is also a pure function,
  so the test worth writing needs no host at all.

- **`LlmCallout` is deliberately not generic**, contrary to the design of record the issue started
  with. `Ask<TResponse>` is generic; the message is not, and the response type rides on it as data.

  A closed `LlmCallout<T>` recovered from a durable inbox on a cold start has no route back to a
  `Type`, because the message type registry is a flat name lookup and the recovery sweep runs before
  the application has published a callout of its own. A generic wire type would therefore require every
  response type to be enumerated at bootstrap. The type argument also carries no behavior at all -- its
  whole job is to name a type twice, once for the schema and once for the deserialization. One message
  type instead buys one handler chain, one dead letter identity, and one place for the budget
  middleware to live. Full reasoning is on
  [#4227](https://github.com/JasperFx/wolverine/issues/4227).

- **Added trim and AOT support.** (closes
  [#4230](https://github.com/JasperFx/wolverine/issues/4230)) `WolverineFx.AI` now sets
  `IsAotCompatible`, on one condition: register response types with `ai.RegisterResponseType<T>()` and
  give `LlmCalloutOptions.JsonSerializerOptions` a source generated `JsonSerializerContext` covering
  them.

  This turned out to need no schema generator. `AIJsonUtilities.CreateJsonSchema(Type, ...)` carries no
  trim annotations at all, because it builds the schema out of whatever `JsonTypeInfo` the serializer
  options resolve -- hand it source generated options and schema generation is already reflection free.
  What did need changing was deserialization, which now goes through the `JsonTypeInfo` overload rather
  than `JsonSerializer.Deserialize(string, Type, JsonSerializerOptions)`, the one that is annotated; and
  identifier to `Type` resolution, which now checks the registration table before falling back to the
  message type registry and `Type.GetType`.

  `LlmCallout.Ask(prompt, context)` keeps its `[RequiresUnreferencedCode]` and `[RequiresDynamicCode]`
  annotations, since serializing an arbitrary object means reflecting over its shape and no amount of
  plumbing changes that. Use `LlmCallout.Ask<T>(prompt).WithContext(context, typeInfo)` instead, which
  serializes through source generated type info, or `WithContext(json)` for context that is already
  JSON. The annotation's message names the replacement right at the call site.

  Guarded by a new `Wolverine.AI.AotSmoke` project under `TrimMode=full`, which CI runs as well as
  builds. Running matters because the failure is silent: a `JsonSerializerContext` missing a response
  type does not throw, it makes `CreateJsonSchema` answer with an empty schema, and the model is handed
  a constraint that constrains nothing. That reads like a model quality problem rather than the
  configuration problem it is, so the smoke test asserts on the rendered schema's contents.

- **Fixed prompt context being serialized with indentation.** `AIJsonUtilities.DefaultOptions` sets
  `WriteIndented`, so every callout carrying context was pretty printing that context into its prompt.
  Whitespace in a prompt is billed input tokens buying nothing, and it inflated every character counted
  against `LlmBudget.MaximumPromptCharacters`. The naming policy and null handling are unchanged, so
  what the model sees is otherwise identical.

### WolverineFx (core)

- **Logical message deduplication, opt-in, on its own table.** (closes
  [#4180](https://github.com/JasperFx/wolverine/issues/4180)) `Envelope.Id` identifies one *delivery*.
  That is the right identity for "the broker handed me this twice" and the wrong one for "the operator
  clicked Rebuild twice" — those are different deliveries of the same intent, so each carries a
  different `Envelope.Id` and every one gets through. `Envelope.DeduplicationId` is now a first-class
  *logical* id with storage, enforcement, and a retention policy behind it.

  Turn it on with `opts.Durability.EnableMessageDeduplication = true`, which provisions a new
  `wolverine_deduplication` table; leaving it off means no schema change at all on upgrade. Mark a
  handler, HTTP endpoint, or gRPC method with `[Deduplicated]` and Wolverine refuses to execute it
  twice for the same id inside `Durability.DeduplicationWindow` (default 24 hours).

  Storage is a separate table rather than a column on `wolverine_incoming_envelopes` on purpose: under
  `EnableInboxPartitioning` the inbox is `PARTITION BY LIST (status)`, and marking an envelope handled
  moves the row between partitions, so a `(deduplication_id, status)` index would let one logical id
  exist as both Incoming and Handled — silently, and only for users who enabled partitioning. A marker
  also has to outlive the inbox row it came from, and the inbox is reaped on a five-minute default.

  Claiming is an `INSERT` that either succeeds or trips the primary key, never a `SELECT`-then-`INSERT`:
  the duplicates this exists to stop are concurrent, and a check-then-act would let both through while
  passing every single-threaded test written against it. A failed non-transactional execution releases
  its claim, so a retry is not discarded as a duplicate of its own failed attempt.

  Refusals differ per chain type: a message handler discards and acks; HTTP returns 409 with
  `ProblemDetails` (configurable to 2xx where a replay is benign); gRPC returns `AlreadyExists` /
  `InvalidArgument` per AIP-193. Storage: PostgreSQL, SQL Server, MySQL, SQLite.

- **The logical deduplication id can now be derived from the message itself.** (closes
  [#4180](https://github.com/JasperFx/wolverine/issues/4180)) The publishing side no longer has to
  remember `DeliveryOptions.DeduplicationId` at every call site — the kind of repetition that gets
  forgotten at exactly one call site and silently un-protects a message, with no failure to see. A
  message type declares its own logical identity once, the way it already declares a topic name with
  `[Topic]` or a saga id with `[SagaIdentity]`:

  ```csharp
  public record ArchiveInvoice([property: DeduplicationIdentity] string InvoiceNumber, DateOnly AsOf);

  [DeduplicationIdentity(nameof(ReceiveShipment.ShipmentId))]   // a contract whose members you cannot decorate
  public record ReceiveShipment(Guid ShipmentId, string Warehouse);
  ```

  and where the identity is not a single member, or the message type is generated and cannot carry an
  attribute at all:

  ```csharp
  opts.MessageDeduplication.ByMessage<RebuildProjection>(x => $"{x.ProjectionName}|{x.OccurrenceUtc:O}");
  opts.MessageDeduplication.ByMemberNamed("IdempotencyKey", "DeduplicationId");
  opts.Policies.ForMessagesOfType<CreateOrder>().DeduplicateBy(x => $"{x.Sku}|{x.Quantity}");
  ```

  All of these are `IEnvelopeRule` at the message type level, resolved once when the route is built
  rather than per message, so they reach every transport, the local queues, and the outbox alike, and a
  rule for an unrelated message type costs nothing on the sending path. Precedence: an explicit
  `DeliveryOptions.DeduplicationId` always wins, then `opts.MessageDeduplication` registrations, then
  `[DeduplicationIdentity]`. No rule overwrites an id that is already set, and a rule returning null or
  empty leaves the message without one — which is how a message opts out. Deriving an id still does not
  deduplicate anything on its own; enforcement remains `[Deduplicated]` plus
  `Durability.EnableMessageDeduplication`.

- **Broker startup is bounded by a clock, not just by an attempt count.** (closes
  [#4116](https://github.com/JasperFx/wolverine/issues/4116)) `BrokerTransport`'s 20-attempt
  initialization loop had no wall-clock budget and an untokened `Task.Delay`, so a host starting against
  a dead broker took **21 minutes 38 seconds** to fail — long enough to look like a hang rather than a
  misconfiguration, and long enough to blow past any orchestrator's startup probe.

- **A `NativeAck` or `Inline` listener reports its real queue depth and last receipt.** (closes
  [#4186](https://github.com/JasperFx/wolverine/issues/4186)) Both reported `QueueCount` 0 regardless
  of actual depth, so monitoring could not distinguish a busy listener from an idle one.

- **`ListeningAgent` now sees past its pass-through receiver wrappers before branching on the
  receiver.** (closes [#4188](https://github.com/JasperFx/wolverine/issues/4188),
  [#4191](https://github.com/JasperFx/wolverine/issues/4191)) `ReceiverWithRules` — installed for any
  incoming envelope rule, which includes a bare endpoint-level `MessageType` or `TenantId` — is
  unconditionally an `ILocalQueue`, so a wrapped `NativeAck` or `Inline` receiver matched the
  `ILocalQueue` branch ahead of its own and threw on the durability agent's re-entry path (DLQ replay,
  scheduled-message firing).

  The same blindness defeated fault detection: `IFaultTrackingReceiver` was type-tested against the raw
  field, which no wrapper implements, so on any non-trivially configured endpoint a terminally faulted
  receiver reported healthy forever and was never rebuilt — the silently-dead-listener case, defeated
  on exactly the endpoints most likely to be configured. Disposal now unwraps too, since the wrappers
  are `IDisposable` only and disposing the outer one would skip `DurableReceiver.DisposeAsync`
  entirely.

  Separately, `StartAsync` wrapped the receiver in `GlobalPartitionedInterceptor` unconditionally while
  the receiver rebuild beside it was guarded. `StartAsync` is also the back-pressure *resume* path, so
  every latch/resume cycle added another interceptor layer with nothing ever removing one — never
  incorrect, since every layer delegates, but an unbounded chain whose per-message cost grew with the
  number of cycles the endpoint had been through.

- **An agent command is never forwarded to the node it is already on.** (#4184) `wolverine_nodes` is
  populated while `NodeController.Agents` is still empty, so during a startup window a node could
  forward a command to itself. Read as a daemon defect for weeks.

- **A shutting-down node no longer dead-letters work whose handler never ran.** (closes
  [#4213](https://github.com/JasperFx/wolverine/issues/4213)) Building the executor for a message type
  resolves services, so a draining node can reach it after the `IServiceProvider` is already gone —
  `IHost.Dispose()` flags the provider *before* it disposes `WolverineRuntime`, whose `DisposeAsync`
  then drains whatever the receiver still holds. Every envelope caught in that window threw
  `ObjectDisposedException` and landed in the GH-4151 catch, which classifies an unbuildable executor
  as a permanent configuration error and moves the envelope to the dead letter queue.

  Shutdown is not a configuration error, and the envelope's handler never ran. `InvokeAsync` already
  had the right guard one level up — leave the envelope unsettled so the broker or the inbox
  redelivers it to a live node — and the GH-4151 catch was simply intercepting first. Surfaced through
  the Redis NativeAck suite, where a draining node dead-lettered five entries and the pending entries
  list went to zero, but the classification is the pipeline's and reaches every transport.

- **A listener whose broker entity was deleted underneath it now heals.** (closes
  [#4215](https://github.com/JasperFx/wolverine/issues/4215)) A wiped broker — LocalStack or the Azure
  Service Bus emulator restarting empty, an operator or IaC teardown — permanently killed every
  listener on it. The receive loop treated every iteration exception the same: log at `Error`, back off
  capped at one second, retry the same receive forever. The entities were declared by `AutoProvision`
  at startup, so the application knows how to declare them; nothing re-ran that afterwards. The tight
  retry also emitted roughly 23 error lines per second across two dead transports.

  `BackgroundReceiveLoop` now takes an optional failure classifier and an optional re-declare step,
  both unset by default. A classified failure reports the new `ReceiveLoopStatus.EntityMissing`, logs
  on the first of a streak and then every sixtieth, backs off on a five second floor, and re-declares.
  Amazon SQS classifies `QueueDoesNotExistException` and Azure Service Bus `MessagingEntityNotFound`.
  Both re-declares are gated on `AutoProvision`: re-creating an entity the application never created is
  not Wolverine's call, and without it the loop still reports and backs off.

- **Terminal settle failures are classified on the block rather than swallowed in a callback.** (closes
  [#4012](https://github.com/JasperFx/wolverine/issues/4012)) All three transports that recognise a
  permanently-unsettleable delivery did it by swallowing inside the retry block's own callback, which
  works and hides the give-up: a swallowed exception is indistinguishable from success at the block's
  boundary, so it could be neither logged differently nor reported. They now declare `ShouldRetry` and
  report through `OnTerminalFailure`.

  Closing the last of GH-4012's five items also closed a gap the previous shape left: Azure Service Bus
  applied its classification by routing callbacks through a shared helper, and two of its four
  listeners — both session listeners — never called it, so they kept burning the full retry budget on
  failures that could never succeed. All four now build their settle block through one factory.

- **`AddStopConditionIfNull` accepts the null identity its signature declares.**
  ([#4161](https://github.com/JasperFx/wolverine/pull/4161)) Both implementations dereferenced the
  nullable identity anyway, so passing the null the signature invites crashed code generation instead
  of producing a guard. With no identity the stock message is now `Required {Type} was not found`
  rather than `Unknown {Type} with identity {Id}` — there is no id, so the message must not ask for
  one. A supplied `MissingMessage` is used verbatim either way, `{Id}` placeholder included: silently
  deleting the placeholder would hide the mistake.

### WolverineFx.RDBMS (all relational providers)

- **Scheduled promotion matches the whole message identity on every provider.** (closes
  [#4216](https://github.com/JasperFx/wolverine/issues/4216)) The PostgreSQL fix shipped for
  [#4202](https://github.com/JasperFx/wolverine/issues/4202) was PostgreSQL-only. Every other relational
  provider has its own `PollForScheduledMessagesAsync`, and SQLite, SQL Server, MySQL and Oracle all
  matched on the id alone with no status predicate. Under `MessageIdentity.IdAndDestination` that
  produced two defects, neither of which needs inbox partitioning: a row sharing the id at another
  destination had its `owner_id` reassigned by a poll that never selected it, and a scheduled sibling
  at another destination that was **not due yet** was promoted to `Incoming` — so a message scheduled
  an hour out executed immediately.

  Each provider now matches `(id, received_at)` as pairs and constrains the update to `Scheduled` rows.
  The pairs have to be matched as pairs: `id IN (...) AND received_at IN (...)` matches the cross
  product, which is the same defect in a longer statement.

- **A redelivered inbox row can be retired when its identity is already handled.** (closes
  [#4216](https://github.com/JasperFx/wolverine/issues/4216)) With `EnableInboxPartitioning` the
  incoming table is partitioned by status and status is part of the key, so one identity can legally
  sit in the incoming partition and the handled partition at once. Marking the incoming row handled was
  then itself a cross-partition move onto a key the handled partition already held, so the row could
  not be retired at all: it stayed `Incoming`, owned by the node that had already processed it, with
  nothing left to try. When a handled row already exists for the identity, the incoming row is now
  deleted instead — the retained handled row is what serves the `KeepAfterMessageHandling` window.

### Diagnostics & telemetry

- **NativeAck and partitioned listeners report numbers an operator can act on.** (closes
  [#4199](https://github.com/JasperFx/wolverine/issues/4199)) Three gaps that GH-4186 exposed when it
  made `QueueCount` real for `Inline` and `NativeAck`.

  `EndpointHealthSnapshot.BufferLimit` is now null on the modes that do not enforce it. Neither
  `Inline` nor `NativeAck` builds a back pressure agent, so filling it in rendered as "234 of 1,000"
  and offered headroom that did not exist. The ceiling that does bound those modes — the broker's
  prefetch window — is reported as the new `InFlightLimit`, from RabbitMQ's `PreFetchCount` and Azure
  Service Bus's `PrefetchCount`.

  A partitioned listener reports `LaneCount`, `BusiestLaneCount` and `ExemptLaneCount`. The aggregate
  depth cannot see the failure partitioning exists to bound: 100 messages spread over ten lanes and 100
  messages piled into one lane report the identical `QueueCount`, and the second is a stalled listener.
  The GH-3899 exempt lane is reported separately because it runs at the endpoint's full parallelism and
  would otherwise read as a hot partition.

  The opt-in in-memory idempotency guard now meters `wolverine-duplicates-suppressed` and
  `wolverine-idempotency-early-rotation`. The second answers whether the window is large enough: it
  counts only rotations forced by the `MaxTracked` ceiling before the window elapsed, which is exactly
  "the effective window is shorter than you configured".

- **`MaximumBrokerRedeliveries` is documented as the delivery count it actually is.** (closes
  [#4216](https://github.com/JasperFx/wolverine/issues/4216)) The implementation permits N deliveries
  and dead-letters the N+1th; the durability guide said so and the XML documentation said "redeliver",
  describing a limit one delivery more generous. The behaviour is unchanged.

### Documentation

- **RabbitMQ: `AddResourceSetupOnStartup` and `AutoProvision`.**
  ([#4223](https://github.com/JasperFx/wolverine/pull/4223))

### WolverineFx.AmazonS3

- **S3-backed document and saga persistence.** (closes
  [#4160](https://github.com/JasperFx/wolverine/issues/4160), from
  [#4165](https://github.com/JasperFx/wolverine/pull/4165) by Anne Erdtsieck) `[Entity]` parameters and
  the declarative `Storage.Store()` / `Insert()` / `Update()` / `Delete()` return values resolve
  against S3 objects. Registration is explicit per type, with the bucket and the key function both
  required:

  ```csharp
  opts.UseAmazonS3Persistence(s3 =>
  {
      s3.Store<InvoiceContent>(x =>
      {
          x.BucketName = "invoice-content";
          x.KeyFor = ctx => $"invoices/v7/{ctx.TenantId}/{ctx.Id}.json";
      });

      s3.Saga<OrderSaga>(x =>
      {
          x.BucketName = "order-sagas";
          x.KeyFor = ctx => $"sagas/{ctx.TenantId}/{ctx.Id}.json";
      });
  });
  ```

  `Store<T>()` and `Saga<T>()` are separate registrations and each refuses the other's type. That is
  what lets the provider claim saga chains *only*: `[Transactional]` and `AutoApplyTransactions` ask
  the same "who owns this chain's transaction?" question, and S3 has no transaction, so an ordinary
  chain that merely touches an S3 document must never resolve here as its transaction owner.

  **Saga writes are conditional; document writes are not.** A document is last-write-wins, because
  `PutObject` overwrites whatever is at the key. A saga is a read-modify-write, so it is written with
  `If-None-Match: *` when starting and `If-Match` against the ETag the session read when updating; a
  `412` becomes `SagaConcurrencyException`, the same exception Marten, EF Core and Cosmos DB raise, so
  one `OnException<ConcurrencyException>` policy still covers every store.

- **The S3 claim check store moves into this package, and
  `WolverineFx.ClaimCheck.AmazonS3` is deprecated.** The types and their namespace are unchanged, so
  migration is a package reference swap and nothing else — but keeping both referenced produces
  ambiguous-type errors. The SQS transport's oversized-message guidance moves with it: two XML comments
  and two runtime error messages were naming a package we are deprecating.

### WolverineFx.AzureBlobStorage

- **Blob-backed document and saga persistence, plus the Azure Blob claim check store.** (closes
  [#4160](https://github.com/JasperFx/wolverine/issues/4160)) The Azure sibling of
  `WolverineFx.AmazonS3`, with the same shape: `blobs.Store<T>()` and `blobs.Saga<T>()` as separate
  registrations, each refusing the other's type, and saga writes guarded by conditional requests.

  Azure Blob Storage does **not** report conditional-write failures the way S3 does, and a straight
  port of the S3 check would have let every duplicate saga start through while looking correct:

  | operation | S3 | Azure Blob Storage |
  |---|---|---|
  | `If-None-Match: *` over an existing object | 412 | **409 `BlobAlreadyExists`** |
  | stale `If-Match` | 412 | 412 `ConditionNotMet` |
  | `If-Match` against a deleted object | 404 | **412 `ConditionNotMet`** |

  Both statuses are translated. The deleted-blob row means completing a saga twice concurrently is a
  concurrency failure rather than a resurrection, with no special case written for it.

### WolverineFx.Redis

- **Redis-backed document and saga persistence.** (closes
  [#4160](https://github.com/JasperFx/wolverine/issues/4160)) Folded into the existing
  `WolverineFx.Redis` rather than shipped as a new package: Wolverine's packages are scoped by
  technology, not by capability. No new project, `.slnx` entry or CI target, and the transport's public
  surface is untouched.

  Saga concurrency is a Lua compare-and-swap rather than `WATCH`/`MULTI`/`EXEC`, which is
  per-connection state that a multiplexed client has to reserve a connection to hold. Redis runs a
  script to completion, which is exactly the atomic read-compare-write a saga needs: one round trip,
  no reserved connection, nothing left dangling by a handler that throws between the read and the
  write. Single key per script, so it is Redis Cluster safe. A create that finds a saga already there,
  an update against a moved-on revision, a completion racing a write, and an update against an
  already-completed saga all surface as `SagaConcurrencyException`.

### WolverineFx.Marten / WolverineFx.EntityFrameworkCore

- **Explicit per-provider entity attributes: `[FromMarten]` and `[FromEfCore]`.**
  ([#4214](https://github.com/JasperFx/wolverine/pull/4214)) `[Entity]` is deliberately store-agnostic,
  and there are two reasons to want an explicit alternative: some teams want the parameter to say where
  it comes from, and Marten's `CanPersist` claims *every* document type, so an entity also mapped in a
  `DbContext` resolves to EF Core by the `IsCatchAll` precedence rule. That default is correct, but
  `[Entity]` had no way to hear that the Marten copy was the one you wanted. The rest of the family
  follows on the same shape.

- **Scope priming no longer manufactures the session it is guarding on.** (closes
  [#4198](https://github.com/JasperFx/wolverine/issues/4198)) Since 6.30.3, every message handler and
  HTTP endpoint that service-located **anything** — for any reason, with or without persistence —
  opened an outbox-enrolled Marten session it never asked for. Cascading messages then left through an
  uncommitted outbox, and under sharded multi-tenancy the session factory threw outright. Requires
  JasperFx 2.58.0 or later.

### Event model

- **A stream-appending handler's return value is a reply, not an event.** (closes
  [#4204](https://github.com/JasperFx/wolverine/issues/4204)) A handler that takes an
  `IEventStream<T>`, appends through it, and returns a DTO for `InvokeAsync<TResponse>` had that DTO
  reported as an emitted event of the slice.

- **A generic message type's slice reads the way source spells it.** (closes
  [#4205](https://github.com/JasperFx/wolverine/issues/4205)) An `IEvent<ClaimReleased>` relay minted a
  slice labelled `` IEvent`1 ``. Unreadable on the canvas, and there was a real problem underneath the
  cosmetic one: `Type.Name` is identical for every closed form of the same open generic, and a slice
  name is the merge key across model sources — so two relays carrying different payloads collided on
  one slice. Only generics are renamed, and all three sources that name a slice go through the same
  rule, because they have to agree or a message reached through both a handler and an HTTP endpoint
  stops merging.

### WolverineFx (core) + event store integrations

- **A locally-owned shard that stopped with nothing to report is restarted.** (closes
  [#4193](https://github.com/JasperFx/wolverine/issues/4193)) An event-subscription agent that stopped
  locally with no `Failure` while keeping its assignment row was invisible to *both* recovery paths,
  because each deferred to the other: the failure sweep saw a null `Failure` and left it to the
  leader's assignment command, and the leader never built one because `AssignedNode == OriginalNode`
  and a local stop does not touch the persisted row. The status was correct, the assignment was
  correct, and nothing joined the two — the shard's sequence simply never moved again.

  Reached through the transient rebuild an operator drives from the console: the rebuild completes,
  acks success, and leaves the shard dead. Verified on a live fleet — frozen at sequence 554 for 161
  seconds across five full assignment cycles before the fix, recovering 8.4 seconds after the rebuild
  with it, three runs out of three.

- **Event Model derivation stops claiming `TriggerLabel`, and reads a collection response as its
  element type.** (closes [#4181](https://github.com/JasperFx/wolverine/issues/4181),
  [#4182](https://github.com/JasperFx/wolverine/issues/4182)) An HTTP slice claimed `TriggerLabel` with
  `"{verb} {route}"` and a gRPC trigger-only slice with `"{service}/{method}"`. Both are *derived*
  claims, so per-role precedence made them beat any label an overlay declared — and minted a
  `SourceDisagreement` hotspot per labelled route for the privilege, so five labelled endpoints meant
  five noise hotspots drowning out any real finding. The claim carried no information either: both
  facts already ride, structured, on `TriggerOrigin`. A trigger label is a *naming* role — "Customer at
  the ATM", which the code cannot express — so it is now left unclaimed and a declaration wins it by
  default. `ApplyExternalSystems` still claims it deliberately, because when a listener triggers a
  slice that already has an origin the external label lives nowhere else.

  Separately, a query chain's response was taken as its read model verbatim, so
  `GET => Task<IReadOnlyList<Order>>` reported the closed generic's assembly-qualified CLR string as a
  canvas node sitting next to the `Order` node its single-document siblings name. A collection response
  now reads its element type. The unwrap walks interfaces rather than matching a whitelist, so Marten's
  `IPagedList<Order>` unwraps too; maps, strings, types that enumerate as two things, and scalar
  elements (a `byte[]` download is not a view over `byte`) are left alone.

### WolverineFx.PostgreSQL

- **A duplicated scheduled identity no longer wedges the scheduled message poller under
  `EnableInboxPartitioning`.** (closes
  [#4202](https://github.com/JasperFx/wolverine/issues/4202)) Partitioning puts `status` in the inbox
  primary key, so uniqueness is only enforced *within* a partition and one identity can legally sit in
  both the scheduled and the incoming partition — a broker redelivery arriving while the previous
  attempt is parked as a scheduled retry produces exactly that pair. Promoting the scheduled row then
  moved it onto an identity the incoming partition already held, and PostgreSQL raised a 23505 unique
  violation. Because the promotion is one statement covering the whole due batch inside the polling
  transaction, that single row rolled back every *other* due scheduled message with it, on every poll,
  indefinitely: scheduled delivery stopped for the whole database until someone deleted the row by
  hand. The poller now discards the superseded scheduled copy — the same identity has since been
  received, or already handled, in another partition — logs a warning naming it, and
  promotes the rest of the batch. Discarding it rather than promoting past a handled row also stops the
  retry re-executing a completed message into a row that could never be marked handled, because marking
  it handled is itself a cross-partition move onto the key the retained row still holds.

- **The scheduled-message promotion is qualified by status and by the configured message identity.**
  This half applies with or without partitioning. The statement previously matched on `id` alone, so it
  could touch rows the poller's own `SELECT` had not produced — including, under
  `MessageIdentity.IdAndDestination`, rows belonging to a different destination that merely shared an
  envelope id. It now matches the key the incoming table was actually built with and promotes only rows
  still in `Scheduled`.

### Dependencies

- JasperFx, JasperFx.Events, JasperFx.Events.SourceGenerator and JasperFx.SourceGenerator to
  **2.57.2**. `JasperFx.RuntimeCompiler` stays on its own 5.x line.

### Testing (`TrackedSession`)

- **A tracked test waits on the fact it asserts, not on a count of envelopes.** (closes
  [#3399](https://github.com/JasperFx/wolverine/issues/3399)) `WaitForExecutionOf` counts *envelopes*,
  not chains, which made a batching test appear to catch a regression that was never there.

- **Scope priming now fires for every chain that service-locates, not only the ones naming an
  `IServiceProvider`.** (closes [#4171](https://github.com/JasperFx/wolverine/issues/4171),
  [jasperfx#715](https://github.com/JasperFx/jasperfx/pull/715)) When generated code falls back to
  service location it creates a child scope, and since GH-3001 Wolverine has seeded that scope so a
  service-located `IMessageContext` / `IMessageBus` — and any instance an integration contributes
  through `WolverineOptions.ScopingFrameSources`, such as Marten's outbox-enrolled `IDocumentSession` —
  is the *same* instance the handler already owns rather than a second, un-enrolled one.

  It only ever worked when something in the chain asked for an `IServiceProvider` by name. The priming
  was composed as a frame, and a frame can only look for the scoped provider during the code
  generator's first resolution pass — but the scope for an *opaque* scoped/transient registration (a
  lambda the container alone can build) is not created until after that pass. So for exactly the chains
  that had no explicit `IServiceProvider`, the frame found nothing, attached nothing, and reported
  nothing: the handler ran against one session while a service-located dependency quietly used another.
  Every test covering the feature happened to put an `IServiceProvider` on the handler signature, which
  is the one shape that did work.

  Priming now attaches where the scope is actually created, so it covers both shapes.
  `ScopePrimingActivatorFrame` is gone.

- **`Lazy<T>` constructor dependencies resolve through their registration instead of being
  inline-constructed.** (closes [#4159](https://github.com/JasperFx/wolverine/issues/4159),
  [jasperfx#715](https://github.com/JasperFx/jasperfx/pull/715)) Code generation ignored an
  open-generic registration such as `services.TryAddScoped(typeof(Lazy<>), typeof(LazyResolver<>))`
  whenever the closed type was itself concrete — which `Lazy<T>` is — and emitted `new Lazy<IFoo>()`
  instead. That compiles and can never work: the parameterless constructor uses
  `Activator.CreateInstance<T>()`, so the first `.Value` throws `MissingMemberException` for any `T`
  without a public parameterless constructor, which is every DI-registered service.

  It failed silently and late. The reporter's host started clean, listeners attached, health checks
  passed, and twelve integration tests recorded zero message execution — no dead letters and no failed
  envelopes, because nothing ever ran.

  Relatedly, `CodeGeneration.AlwaysUseServiceLocationFor(typeof(Lazy<>))` accepted an open generic and
  then matched nothing, so it was a silent no-op; it now matches that generic's closed forms, and only
  those. A concrete generic with no registration of its own still gets built inline as before.

- **A `TypeLoadMode.Static` assembly mismatch now fails the host start instead of the first message.**
  (closes [#4151](https://github.com/JasperFx/wolverine/issues/4151)) `Static` mode loads pre-built handler
  types out of `WolverineOptions.ApplicationAssembly`, but `codegen write` emits its source into the entry
  project. When handlers live in a class library and the app points `ApplicationAssembly` at that library the
  two disagree, and nothing detected it — not `codegen write`, not the build, not host start. The host booted
  healthy and `StaticTypeLoader` threw on the first dispatched message. `HandlerGraph` now attaches every
  expected pre-built type up front and throws `MissingPreBuiltTypesException` naming the chains it could not
  load, where the generated types actually landed, and how to reconcile the two assemblies.

- **An envelope whose executor cannot be built is dead-lettered rather than acked away.**
  (closes [#4151](https://github.com/JasperFx/wolverine/issues/4151)) That throw happens before any
  `HandlerChain` instance exists, so no `chain.Failures` rule could ever apply — not even a `MoveToErrorQueue`
  rule written for that message type — and the pipeline's last-resort recovery completed the message. On a
  durable transport the row was marked `Handled` with `attempts=0` and then swept by ordinary inbox cleanup:
  byte for byte the lifecycle of a message that succeeded, with an empty dead letter table and a healthy host.
  The reporter lost every message of one type in production this way. A missing pre-built type, a chain that
  will not compile, and a sticky-handler misconfiguration all take this path, and none of them succeeds on a retry.

- **A failed message now reaches a terminal tracking state, and failures are no longer invisible to
  `IMessageTracker`.** (closes [#4136](https://github.com/JasperFx/wolverine/issues/4136))
  `WolverineRuntime.MessageFailed` recorded `MessageEventType.Sent`, which is not terminal, so a session
  containing a failed message could only end by timing out. Separately,
  `HandlerPipeline.RecoverFromFailedProcessingAsync` emitted no message event at all — an envelope that died
  there was invisible to every `IMessageTracker`, so **failure metrics under-counted every such envelope**.
  Note this is visible beyond tests: wire taps and custom `IMessageTracker` implementations now see a
  `MessageFailed` event where they previously saw a `Sent`. The dead-letter path now reports exactly one
  terminal event — `MovedToErrorQueue`, carrying the exception — which also fixes a double-counted dead-letter
  metric, a doubly-recorded effective time, and a failure wire tap firing twice for a single envelope.

- **A batched message type gets one `BatchingProcessor`, not one per racing caller.**
  (closes [#4167](https://github.com/JasperFx/wolverine/issues/4167)) `BatchingOptions.BuildHandler` was an
  unguarded `if (_handler != null)` lazy init, and it runs on the **message-handling path**, not at bootstrap.
  Callers reach it through `HandlerPipeline`'s `LightweightCache<Type, IExecutor>`, whose indexer does not lock:
  two concurrent misses each invoke the factory **and each returns its own instance**. That is harmless for a
  stateless executor, which is why the cache is fine everywhere else — but a `BatchingProcessor` owns a
  `BatchingChannel` buffer, a flush `Timer`, and two `Block`s with live worker tasks. Two instances means
  **members of one logical batch are split across two buffers and flushed as separate batches**, and the losing
  instance is never handed back to any caller, so nothing disposes it: **its timer and worker tasks leak for the
  life of the process**. Reachable on any concurrent first-touch of a batched message type; the JasperFx change
  below made it deterministic by removing the inline execution that had been serializing those first messages.

- **`Received` is reported exactly once per receipt on a partitioned listener.**
  (closes [#4135](https://github.com/JasperFx/wolverine/issues/4135)) A listener with
  `PartitionProcessingByGroupId` recorded **two** `Received` events for one envelope while executing it exactly
  once: `ShardedExecutionBlock.DeserializeFirst` has to deserialize up front, so the envelope reached
  `executeAsync` already carrying a `Message` and took a second reporting branch. `IMessageTracker.Received` is
  not only tracking — the same call increments the received counter, posts `RecordReceived` to the CritterWatch
  accumulator, and logs the receipt. **Every partitioned listener has been double-counting received messages in
  production metrics and logging each receipt twice.** `GlobalPartitionedInterceptor` took the same route and is
  fixed by the same guard.

- **An `OnException` method returning the type its chain handles now compiles.**
  (closes [#4139](https://github.com/JasperFx/wolverine/issues/4139)) The return value took its default variable
  name from its type, and so does the chain's message body variable; the catch block is emitted inside the scope
  that declares the body, so the generated handler failed with CS0136 and **the chain then never ran**. Silent
  from two directions — the cascading message is still sent, so coverage asserting on `session.Sent` stayed green.

- **`MessageRoute.Describe()` no longer throws a `NullReferenceException` under description.**
  (closes [#4132](https://github.com/JasperFx/wolverine/issues/4132)) A route built under
  `WolverineSystemPart.WithinDescription` against an endpoint the runtime never compiled has a legitimately null
  `Serializer`, and because `GlobalPartitionedRoute.Describe()` maps over every slot, one such route took out
  `wolverine-diagnostics describe-routing` for the whole application.

### Testing (`TrackedSession`)

- **`DoNotAssertOnExceptionsDetected()` no longer suppresses the tracked-session timeout assertion, and
  `DoNotAssertOnTimeout()` is added.** (closes [#4125](https://github.com/JasperFx/wolverine/issues/4125))
  `AssertNotTimedOut()` shared a flag with `AssertNoExceptionsWereThrown()`, so a resiliency test reaching for
  that method for its documented reason — handlers are *expected* to throw — silently also opted out of the only
  assertion that catches a session having completed none of its work. **The result was a permanently green test
  over a session that did nothing.** The session's own timeout is not an "exception detected during the message
  activity"; it is the harness reporting that the activity never finished, so the assertion is ungated rather
  than moved behind a second flag. Ungating it surfaced exactly three real defects across CoreTests
  (see #4136 and #4139 above) plus four more suites that were green over a session that timed out. Tests that
  legitimately expect activity *not* to happen now use the separate, accurately-named `DoNotAssertOnTimeout()`.

### WolverineFx (core) + event store integrations

- **A service-located session is the handler's session on RavenDb, Polecat and Fisher too.**
  (closes [#4145](https://github.com/JasperFx/wolverine/issues/4145)) GH-3001 primed Wolverine's child
  `IServiceScope` with the handler's outbox-enrolled `IDocumentSession` for Marten only; on the other three
  stores a service-located session was a **separate, un-enrolled one whose writes escaped the transaction the
  middleware commits**. RavenDb was the worst of the three — `Wolverine.RavenDb` registered no
  `IAsyncDocumentSession` in DI at all, so a service-located one could not resolve. `UseRavenDbPersistence()`
  now owns that registration, decorating rather than replacing an application's own. That last part is an
  **additive behavior change**: `IAsyncDocumentSession` becomes DI-resolvable where it previously was not.

- **`OutboxedSessionFactory` resolves the `Main` message store lazily.**
  (closes [#4130](https://github.com/JasperFx/wolverine/issues/4130)) Both providers captured
  `MessageStore = runtime.Storage` in the constructor, but that is the placeholder `NullMessageStore` until
  `MessageStoreCollection.InitializeAsync()` assigns the real one — deferred whenever more than one store claims
  `Main`. A factory constructed during startup kept the placeholder for the life of the process while
  `Stores.Main` read perfectly correct afterwards: the host booted and listened cleanly, then **failed every
  message and HTTP request**. The reproducing shape is ordinary — an event-store-integrated `Main` plus a
  database-backed queue transport, which is the whole broker-less deployment mode.

### WolverineFx.Pulsar

- **A Pulsar listener is not "started" until its subscription exists at the broker.**
  (closes [#4149](https://github.com/JasperFx/wolverine/issues/4149)) DotPulsar's `IConsumerBuilder.Create()`
  returns as soon as the consumer object exists; the `Subscribe` command travels to the broker on a background
  task. `IHost.StartAsync()` therefore returned while the topic did not yet exist at the broker at all —
  measured against the admin API, HTTP 404 five runs out of five. Since `SubscriptionInitialPosition` defaults
  to `Latest`, anything published into that window **is not delivered and is not recoverable**: silent message
  loss on first deployment of a service that publishes to a topic it also listens to.
  `BuildListenerAsync` now awaits subscription establishment for the primary consumer, the retry-letter
  consumer, and every per-tenant listener, bounded at 10s and logged rather than thrown.

- **A Pulsar receiving loop no longer dies silently on one exception.**
  (closes [#4100](https://github.com/JasperFx/wolverine/issues/4100)) Both loops were a bare `await foreach`
  inside a fire-and-forget `Task.Run` with no `try` anywhere and nothing observing the returned task. Anything
  that threw killed that task for good while the consumer stayed alive, the socket stayed open, and
  `ConnectionState` went on reporting `Connected` — the silently-dead listener RabbitMQ fixed in #3391. The
  loops get a per-message guard that logs and skips (deliberately leaving the message unacknowledged, since a
  mapper or codec failure is deterministic and deferring would redeliver into the same exception forever) and an
  enumeration guard that logs and re-enters `Messages()` after a one-second pause. Disposal is fixed on the same
  path: `Task.Dispose()` throws on a task that has not reached a final state, and the linked
  `CancellationTokenSource` was never disposed.

### WolverineFx.EntityFrameworkCore

- **`StartDatabaseTransactionForDbContext` no longer runs the eager idempotency check twice.**
  (closes [#4128](https://github.com/JasperFx/wolverine/issues/4128)) It emitted `AssertEagerIdempotencyAsync`
  under identical guards both above and below the `BeginTransactionAsync` block. Where that falls through to
  `Runtime.Storage.Inbox.ExistsAsync` — which does not set `Envelope.WasPersistedInInbox` — it ran **two
  identical inbox existence queries per message**.

### Diagnostics & telemetry

- **Node control endpoints stop publishing OpenTelemetry spans for agent commands.**
  (GH-1670 / GH-907 follow-up) The receive and execution spans are gated by the *endpoint's* `TelemetryEnabled`
  flag, not the agent-command chain's, so a broker control queue (`EnableWolverineControlQueues` on RabbitMQ,
  Azure Service Bus or SQS) or a TCP control endpoint still published send, receive and execution spans for
  every `IAgentCommand` it carried, while the database control endpoint was quiet. The `NodeControlEndpoint`
  setter now switches telemetry off along with the role promotion. **Known trade-off:** the broker control
  queues also set `IsUsedForReplies`, so on hosts that opt into them, replies to cross-node request/reply
  arriving on the control queue lose their receive span as well.

- **`event-model --url` PUTs the assembled descriptor to a monitor.**
  (closes [#4146](https://github.com/JasperFx/wolverine/issues/4146)) So the design-time loop is one command:

  ```bash
  dotnet watch run -- event-model --url http://localhost:5525
  ```

  `--json`'s default moves from `"event-model.json"` to null so `--url` on its own does not also litter the
  application directory; with neither flag the command still writes `event-model.json` exactly as before.
  Wolverine takes no reference on the monitor — it is an HTTP PUT to whatever URL is named — and a monitor that
  is down fails with a one-line message and a non-zero exit rather than a stack trace, because under
  `dotnet watch` a console that has not been started yet is the ordinary case.

- **Event model sources declare `EventModelProvenance.Derived`.**
  (closes [#4152](https://github.com/JasperFx/wolverine/issues/4152),
  [#4147](https://github.com/JasperFx/wolverine/issues/4147)) `WolverineEventModelSource` and
  `HttpEventModelSource` both read their roles off compiled chains, so both now say so. That lets the
  `services.Insert(0, ...)` in `UseWolverine()` and `MapWolverineEndpoints()` become plain `AddSingleton` calls —
  the insert was load-bearing behaviour that nothing in the registration explained. Note the deliberate
  inversion this buys: a source that **observes** a running system now outranks a derived one.

### Dependencies

- **JasperFx 2.56.0.** Adopted for the `EventModelProvenance` ladder above.

- **JasperFx 2.57.0.** A `Block` no longer runs its action on the **publisher's** thread
  ([#4167](https://github.com/JasperFx/wolverine/issues/4167),
  [jasperfx#714](https://github.com/JasperFx/jasperfx/pull/714)). `Block` built its channel with
  `AllowSynchronousContinuations = true`, so a reader parked in `WaitToReadAsync` was resumed by `TryWrite` on
  the publishing thread and `Post()` executed the action **inline instead of enqueuing it**. A buffered local
  queue therefore got **no parallelism at all** once its workers went idle: measured on a queue configured with
  `MaximumParallelMessages(5)`, a burst of 20 published messages all ran inline and serialized on the publishing
  thread — and that thread, which may be a broker listener loop or an HTTP request, stalled for the full
  duration of every handler. Note the reported ".NET 10 regression" framing is only half the story: an
  **unbounded** channel — what a buffered local queue uses — ran continuations inline on *every* runtime, so
  local queues were never net10-specific. What [dotnet/runtime#116021](https://github.com/dotnet/runtime/pull/116021)
  changed is the **bounded** case, which is what broker-backed `BufferedReceiver`s and `DurableReceiver` use.

- **Weasel 9.27.0.** Nine fixes, every one a case where Weasel read a schema back as something other than what
  the database actually held, and **no DDL changes** for a schema it was already reading correctly. Three of
  them could never converge — the patch is applied, the read-back is still different, and the next run produces
  the identical patch indefinitely: a **named foreign key on SQLite** rebuilt the table and copied every row on
  every run ([weasel#516](https://github.com/JasperFx/weasel/issues/516)); a **partition-aligned index on SQL
  Server** was dropped and recreated on every run, because the implicit `sys.index_columns` row for the
  partitioning column was read as a declared key column, so the index compared unequal to itself
  ([weasel#512](https://github.com/JasperFx/weasel/issues/512)); and a **concurrent index on a manager-owned
  partitioned table** stayed permanently invalid, strictly worse than the blocking `CREATE INDEX` it replaced
  ([weasel#520](https://github.com/JasperFx/weasel/issues/520)). Full notes in Weasel's
  [upgrade guide](https://weasel.jasperfx.net/release-9-27).

### Documentation

- **The concurrent-duplicate bound belongs to group partitioning, not to `EndpointMode.NativeAck`.**
  (closes [#4127](https://github.com/JasperFx/wolverine/issues/4127)) `listeners.md` stated "a duplicate never
  runs concurrently with the original" as a property of the mode. It is a property of group partitioning, which
  is opt-in via `PartitionProcessingByGroupId()` and never on by default; an unpartitioned `NativeAck` endpoint
  is bounded by `MaximumParallelMessages()` instead. Both the guarantee and the #3713 measurements that inherit
  it are now scoped.

- **`tutorials/idempotency.md` no longer contradicts itself.**
  (closes [#4129](https://github.com/JasperFx/wolverine/issues/4129)) It warned that every explicit idempotency
  usage outside a durable listener falls back to `Eager`, then said a few lines later that "Marten supports both
  modes." The warning is the accurate one — Marten, Polecat, Fisher and EF Core all emit the `Eager` check for
  either style — and that is now mirrored onto the `IdempotencyStyle.Optimistic` XML doc, which described a live
  choice.

- `MaximumBrokerRedeliveries` and `MaximumAckAttempts` are documented
  ([#4012](https://github.com/JasperFx/wolverine/issues/4012)); `[StreamState]` and `[StreamEvents]` are
  documented ([#3627](https://github.com/JasperFx/wolverine/issues/3627)); the NATS native-ack badge is
  corrected to 6.30 ([#4053](https://github.com/JasperFx/wolverine/issues/4053)).

### WolverineFx.Http

- **`ServiceProviderSource.IsolatedAndScoped` is honored, and the isolated scope is primed.**
  (closes [#4171](https://github.com/JasperFx/wolverine/issues/4171)) An endpoint or middleware that asked
  for an `IServiceProvider` always received `httpContext.RequestServices`, whatever `ServiceProviderSource`
  said. Two separate paths bound it: `HttpChain.HttpContextVariables` exposes every `HttpContext` property as
  a derived codegen variable, and derived variables outrank every variable source; and `HttpContextElements`,
  an `IParameterStrategy`, matches any parameter whose type equals an `HttpContext` property type, which it
  did first. Both now leave `IServiceProvider` alone and let the normal service machinery answer it, so
  `IsolatedAndScoped` gets Wolverine's own child scope and `FromHttpContextRequestServices` still gets
  `httpContext.RequestServices`.

  HTTP chains also never took part in the GH-3001 scope priming that handler chains have, so a
  service-located `IMessageContext` / `IMessageBus` — or a persistence session contributed through
  `WolverineOptions.ScopingFrameSources`, such as Marten's outbox-enrolled `IDocumentSession` — was a
  *different* instance from the one the endpoint already owned. They do now; see the core entry below,
  which fixes the same gap for message handlers.

  ::: warning
  Requesting an `IServiceProvider` in an endpoint is service location, and it now registers as such. Under
  `ServiceLocationPolicy.NotAllowed` those endpoints will throw where they previously slipped through
  unnoticed — message handlers have always behaved this way, and HTTP now matches.
  :::

- **HTTP chains carry their Event Modeling roles.** ([#3988](https://github.com/JasperFx/wolverine/issues/3988))
  Every routed `HttpChain` derives the same slice a message handler does — triggered by its verb + route, the
  request body as the command, the endpoint type as the handler, `[WriteAggregate]` / `[ReadAggregate]` and friends
  as aggregates and read models, `[EmptyResponse]` returns as emitted events, and a `GET` that writes nothing as a
  `View` slice reading its resource type — through an `IEventModelDefinitionSource` that `AddWolverineHttp()`
  registers, so it reaches `ServiceCapabilities.EventModel` and the `event-model` export with no further wiring.

- **`Before` middleware can replace an immutable request body.**
  ([#3984](https://github.com/JasperFx/wolverine/pull/3984)) Handler chains have supported this since
  GH-516: a `Before` / `BeforeAsync` that accepts the message type *and* returns it overwrites the message
  for the rest of the chain. HTTP chains had no equivalent pass, so the same shape on an endpoint class
  failed to compile with a colliding local (CS0841/CS0136).

  ```csharp
  public static class StampedRequestEndpoint
  {
      // Accepts the request type and returns it, so it replaces the
      // request body for the rest of the chain
      public static StampedRequest Before(StampedRequest request)
          => request with { StampedBy = "server" };

      [WolverinePost("/middleware/stamped")]
      public static string Handle(StampedRequest request) => request.StampedBy;
  }
  ```

  Use it to enrich an immutable `record` request with server-supplied values — timestamps, user identity —
  before the endpoint method runs, keeping the endpoint signature a pure decider. Sync, async and
  tuple-returning `Before` methods behave exactly as they do on the handler side, including returning the
  replaced request alongside a short-circuiting `IResult`.

  Middleware registered externally (`Policies.AddMiddleware`, `[Middleware]`) still may not return the
  request type; that remains blocked by the existing `MiddlewarePolicy` exception.

### WolverineFx.RavenDb / WolverineFx.Sqlite

- **`ClearAllAsync` could not delete node records another process wrote.**
  ([#3993](https://github.com/JasperFx/wolverine/pull/3993), closes
  [#3986](https://github.com/JasperFx/wolverine/issues/3986)) Clearing node state is what a `Solo`-mode
  start does to sweep the `WolverineNode` records a previous `Balanced` run left behind — so by
  construction it deletes rows *this* process did not write.

  On **RavenDB** it could not. `LoadAllNodesAsync` opens and disposes its own session, and RavenDB's
  `Delete<T>(T entity)` requires the entity to be tracked by the session it is called on, so every stale
  node threw and the application could not start:

  ```
  System.InvalidOperationException: Wolverine.Runtime.Agents.WolverineNode is not associated
  with the session, cannot delete unknown entity instance
  ```

  Node records are now deleted by document id, which is what `DeleteAsync(Guid, int)` and the agent
  assignment deletes in the same loop already did. The reported workaround — clearing
  **Documents → WolverineNodes** by hand in RavenDB Studio before a Solo start — is no longer needed.

- **SQLite orphaned every agent assignment row.** ([#3993](https://github.com/JasperFx/wolverine/pull/3993))
  The compliance coverage written for the RavenDB fix caught a second provider. SQLite's assignment table
  declares no foreign key to the node table — PostgreSQL, Sql Server and Oracle all use
  `ON DELETE CASCADE`, and SQLite would not enforce a constraint anyway without `PRAGMA foreign_keys=ON`
  per connection — so both clearing all nodes and deleting a single node left the assignment rows behind
  permanently.

  Those orphans are invisible to `LoadAllNodesAsync`, which only attaches an assignment to a node id it
  actually loaded, right up until a node re-registers under the same id — exactly the GH-3604 ejection
  path — where it returns owning agents it was never reassigned. Both statements now delete the
  assignments explicitly. Existing databases simply stop accumulating them; any already-orphaned rows are
  cleared by the next sweep.

  The underlying gap was that `NodePersistenceCompliance` never exercised `ClearAllAsync` at all, which is
  how two providers could ship it broken. It does now.

### WolverineFx (core)

- **`.ExternalSystem("Stripe")` names the external system on an endpoint, and the Event Model renders the
  boundary.** ([#3989](https://github.com/JasperFx/wolverine/issues/3989)) Every listener and subscriber
  configuration gains `ExternalSystem(string name)` (stored as `Endpoint.ExternalSystemName`, surfaced as the typed
  `EndpointDescriptor.ExternalSystem` in capabilities). The Wolverine-derived Event Model attaches an inbound
  external-system element to the slice a named listener triggers — a handler stuck to it, or the handler of its
  `DefaultIncomingMessage<T>()` — making it a `Translation` slice triggered `External` (a named listener bound to
  no slice still renders as a trigger-only boundary), and an outbound element to every slice whose published
  messages or emitted events the named endpoint subscribes to. The edge is derived; only the name is declared,
  on the endpoint, never in the overlay (jasperfx#687 decision 5).

- **Chains carry their Event Modeling roles, and `event-model` exports them.**
  ([#3988](https://github.com/JasperFx/wolverine/issues/3988), [#3990](https://github.com/JasperFx/wolverine/issues/3990))
  Every message handler chain now derives its Event Modeling slice — command, handler, the aggregate(s) it decides
  against (`[WriteModel]` / `[DeciderFunction]` / `[DcbModel]` and the store spellings), emitted events from its
  declarative returns, read models (`[ReadModel]`, `[Entity]`, `IStorageAction<T>`), cascaded messages, trigger kind
  (message handler / job scheduler / gRPC when an RPC forwards the message) and slice pattern — as a JasperFx
  `EventModelSliceDescriptor`. It is surfaced on `MessageHandlerDescriptor.EventModel`, as the assembled
  `ServiceCapabilities.EventModel`, and through a `WolverineEventModelSource : IEventModelDefinitionSource` that
  `UseWolverine()` registers ahead of every overlay so derived roles win on merge. `dotnet run -- event-model
  [--json <path>]` writes the merged model as JSON from a host that is built but never started — no transports,
  no database, no runtime compiler. Imperative `session.Events.Append(...)` in a handler body stays invisible by
  decision of record; only declarative returns are reported. Bumps JasperFx to 2.54.0 for the descriptor.

- **An agent this node cannot build is released to a node that can.**
  ([#3994](https://github.com/JasperFx/wolverine/pull/3994), closes
  [#3970](https://github.com/JasperFx/wolverine/issues/3970)) When `IAgentFamily.BuildAgentAsync` threw,
  the exception was caught, logged, and dropped. The agent was simply absent from the confirmed set, so
  the leader learned only that it was *unconfirmed* — which it deliberately does not treat as a failure,
  because GH-3750 fixed exactly the opposite bug — and the assignment stood. The same agent was requested
  on the same node again on the next tick, forever. Reported as a 54-minute fleet-wide projection stall on
  a blue/green cluster whose two sides carry disjoint projection versions.

  GH-3888's release path is the right remedy but structurally could not reach this: it is driven by the
  stall detector sweeping the agents a node is actually *running*, and a start that throws leaves no
  instance at all, so no restart budget was ever consumed.

  Consecutive failed starts are now counted on the node that catches them — the only place that can tell
  "still working on it" from "threw, and will throw again here" — and an exhausted budget feeds into the
  existing release path. A live peer must still advertise the capability, the capability embargo still
  stops the leader handing the agent straight back, and the assignment row is dropped so the agent can be
  placed elsewhere.

  The new `DurabilitySettings.MaxAgentStartFailuresBeforeRelease` (default `3`) is the counterpart to
  `MaxLocalAgentRestartsBeforeRelease`. Each tick already spends `AgentStartRetryAttempts + 1` inner
  attempts before it counts as one failure here, so the default is three strikes across three assignment
  cycles. Set it to `0` to restore the previous behaviour. The count is *consecutive* — a successful start
  clears it, and so does any stop.

  Durability agents (`wolverinedb://`) are never in a node's advertised capabilities, so they always take
  the decline branch and their local retries are unchanged. That is correct: there is no capability-matched
  alternative node to release them to.

### Dependencies

- **RabbitMQ.Client 7.1.2 → 7.2.2.** Nine months of client fixes, several of them in exactly the
  connection- and channel-churn area Wolverine's listeners live in: races in `AsyncManualResetEvent`,
  a `SemaphoreFullException`, a connection leak in `AutorecoveringConnection`, publisher confirms being
  handled after dispose, `TryComplete` during channel shutdown, heartbeat callbacks no longer crashing
  the process on an exception, and recovery retried when a topology operation times out.

  This deliberately does **not** fix [#3950](https://github.com/JasperFx/wolverine/issues/3950).
  `SessionManager.Lookup` still reads its session map with an indexer, and is byte-identical in v7.1.2,
  v7.2.2, and the client's `main` — so one rejected delivery tag on a busy channel can still escalate
  into a library-initiated close of the whole connection. That fix has to happen upstream.

- **Weasel 9.26.0.** Two defects that Wolverine's new fail-fast migrations (below) turned from a logged
  line into a failed startup: `Table.FetchExisting` filtered the PostgreSQL catalog with
  `NOT nspname LIKE 'pg%'`, which hid every index in a *user* schema whose name began with "pg", so those
  schemas re-issued `create index` on every start and answered `42P07`
  ([weasel#504](https://github.com/JasperFx/weasel/issues/504)); and Sql Server's column drop did not first
  drop the auto-named default constraint that depends on the column, so a column declared with a default
  could be added by a migration but never removed by one
  ([weasel#505](https://github.com/JasperFx/weasel/issues/505)) — reachable from Wolverine configuration by
  turning `OutboxStaleTime` / `InboxStaleTime` back off.

### WolverineFx.RDBMS (all relational providers)

- **A multi-part schema name is rejected where it is configured.**
  (closes [#3997](https://github.com/JasperFx/wolverine/issues/3997)) Wolverine renders each of its tables
  as `{schema}.{table}` with no delimiting, so a "schema name" that is itself multi-part produces a name
  with more parts than any supported engine accepts:

  ```
  opts.PersistMessagesWithSqlServer(cnx, "crm.sales.opportunities");

  // The object name 'crm.sales.opportunities.wolverine_node_records' contains more than
  // the maximum number of prefixes. The maximum is 2.
  ```

  Weasel's `CREATE SCHEMA` is the one statement that *does* delimit the name, so the schema was created
  and only the tables failed — and, because those failures were swallowed (below), startup then died a
  long way from the cause, in `LoadNodeAgentStateAsync`, against `Could not find server 'crm' in
  sys.servers`: SQL Server reads a four-part name as *server.database.schema.object*. PostgreSQL reaches
  the same place through `improper qualified name (too many dotted names)`.

  Every schema name Wolverine accepts — `PersistMessagesWithXXX`, the database-backed transports'
  `TransportSchemaName`, and the `MessageStorageSchemaName` on the Marten, Polecat and Fisher integrations
  — now throws an `ArgumentOutOfRangeException` naming the offending value the moment it is set. A name you
  have already delimited (`"[crm.sales]"`) is rejected as well: the schema is created and the first start
  succeeds, but the `CREATE SCHEMA` guard compares `sys.schemas.name` against the bracketed spelling, so it
  never matches and every restart re-issues the `CREATE SCHEMA` against a schema that now exists. Schema
  difference detection cannot match a delimited name against the catalog either, so the store re-applies its
  whole DDL every start and never picks up a later column-level change.

- **Failed storage migrations are no longer swallowed.**
  (closes [#3997](https://github.com/JasperFx/wolverine/issues/3997)) Weasel hands a failed DDL statement
  to the `IMigrationLogger` rather than throwing, on the grounds that a non-default logger means the caller
  wants to decide — and Wolverine's decision was to log and carry on, so a host could start against storage
  that had never been created. It now throws a `WolverineSchemaException` carrying the SQL that failed,
  which is what Marten's equivalent logger has always done.

  Hosts that would rather start up anyway can keep the old behavior with
  `ResourceMigrationFailureMode.ContinueOnFailures`. Note that Wolverine already serializes migrations
  across processes with a global advisory lock and retries the whole migration once after a short delay,
  so a genuine race between two nodes starting at the same instant does not need it.

### WolverineFx.PostgreSQL / WolverineFx.SqlServer / WolverineFx.Oracle

- **The orphaned-message sweep is now indexable, bounded, and outside the shared recovery transaction.**
  ([#3995](https://github.com/JasperFx/wolverine/pull/3995), closes
  [#3971](https://github.com/JasperFx/wolverine/issues/3971)) The sweep that releases inbox and outbox
  messages owned by departed nodes was three problems at once, all of which compound at scale. Reported
  against a 466-shard PostgreSQL deployment where it had become the dominant source of database load and
  lock contention.

  **The predicate could not use an index.** `owner_id != 0 and owner_id not in (<live nodes>)` — the
  selective part is the `NOT IN`, and everything else matches essentially every row, because in a healthy
  fleet virtually every envelope is owned by a *live* node. So it was a full scan of the whole inbox, per
  database, every five seconds, finding nothing; an index on `owner_id` did not change the plan. The dead
  owners are now determined first and the update asks for `owner_id in (<dead>)`.

  Worth knowing if you are reasoning about this yourself: the obvious `select distinct owner_id` measured
  *worse* than the predicate it replaces (355 buffers / 8.9 ms against 837 / 3.0 ms), because PostgreSQL
  will not plan a loose index scan on its own. PostgreSQL therefore spells it as an explicit recursive
  skip-scan — 8 buffers, 0.026 ms, cost proportional to the number of distinct owners rather than the row
  count. Sql Server keeps a portable `DISTINCT`, because T-SQL forbids aggregates, `TOP` and subqueries in
  the recursive member of a recursive CTE.

  **The update was unbounded.** One node loss made every envelope it owned qualify in a single statement —
  82,520 rows in one `UPDATE` on one shard, roughly 910,000 across the fleet, at ~12 KB a body. It is now
  bounded by the new `OrphanedMessageReleaseBatchSize` (PostgreSQL by `ctid`, Sql Server by `update top`)
  with a per-cycle cap. Providers that cannot bound it fall back to a single statement, as they already do
  for the expired-handled cleanup.

  **It ran inside the shared recovery transaction** — the one #3116 deliberately moved cleanup deletes out
  of, for exactly this reason. The reported symptom was `TimeoutException` on inbox inserts with every
  blocking session sitting on this statement. It now runs on its own timer, in its own transaction, on its
  own `OrphanedMessageSweepPollingTime`, so slowing the sweep no longer delays scheduled message delivery.

  Also ships the partial index on `owner_id` that the new predicate makes worthwhile. **Existing databases
  will apply that index on the next migration.**

- **The sweep never released anything on Oracle.**
  ([#3995](https://github.com/JasperFx/wolverine/pull/3995)) Caught by CI before release rather than after.
  Both of the sweep's reads are of node numbers, and both went through `FetchListAsync<int>()`, which reads
  via `GetFieldValueAsync<int>()`. Oracle's `NUMBER` arrives from ODP.NET as an `Int64`, so both threw
  `InvalidCastException`.

  Both reads sit inside the `try`/`catch` that logs and returns, so on Oracle the whole sweep degraded to a
  **silent no-op** — it released nothing, forever, while looking healthy apart from one log line per cycle.
  Node numbers are now converted rather than cast, which tolerates whatever integral type a provider
  surfaces. Oracle users on 6.29.2 get a sweep that works; there was never a released version where it did,
  since the sweep itself is new here.

### WolverineFx.MySql

- **The envelope storage migration no longer fails on every startup.**
  ([#3985](https://github.com/JasperFx/wolverine/pull/3985), closes
  [#3983](https://github.com/JasperFx/wolverine/issues/3983)) On MySQL every node logged an `Error` from
  `MySqlMessageStore` on every start: `Cannot drop index 'fk_wolverine_node_assignments_node_id': needed
  in a foreign key constraint`. The first migration against an empty database is a pure `CREATE`, so this
  was unreachable until the second run — it reproduced against a brand-new database and then repeated
  forever.

  There were four drift items, not one, and only the first threw — so the migration runner logged it and
  the rest never ran. `wolverine_nodes.node_number` and `wolverine_node_records.id` folded `AUTO_INCREMENT`
  into the column *type* string; the catalog reports the type as plain `INT` and carries `auto_increment`
  separately, so neither column ever compared equal and both emitted a `MODIFY COLUMN` on every check.
  They now use Weasel's `AutoIncrement()`. The generated DDL is unchanged apart from where the keyword
  sits, so **existing databases need no migration**.

  The other three were `Weasel.MySql` bugs, fixed in [weasel#445](https://github.com/JasperFx/weasel/pull/445)
  and shipped in Weasel 9.25.0.

### WolverineFx.Oracle

- **Oracle schema migration passes the Oracle command builder.**
  ([#3985](https://github.com/JasperFx/wolverine/pull/3985)) `SchemaMigration.DetermineAsync(conn, ct, objects)`
  builds a plain `DbCommandBuilder`, whose `StartNewCommand()` is a no-op — correct for providers that
  accept several statements in one command, wrong for Oracle. Weasel 9.25 raised Oracle's table
  introspection from one query to six ([weasel#474](https://github.com/JasperFx/weasel/issues/474)), so
  those boundaries stopped being decorative: without them ODP.NET receives one command holding six
  `SELECT`s and rejects it with `ORA-03048`.

  Every Oracle call site now passes `OracleMigrator.CreateCommandBuilder(conn)`, which is what Weasel
  documents for this. Nothing outside Oracle is affected — the no-op is correct everywhere else.

### Dependencies

- **Weasel bumped to 9.25.1**, the identifier release. If your schema names are all plain lowercase
  identifiers the emitted DDL is byte-identical to 9.24 and there is nothing to do. Otherwise three
  changes are worth knowing about, and Weasel's
  [upgrade notes](https://weasel.jasperfx.net/release-9-25) carry the full list:

  - **Oracle can now see index, foreign key and primary key drift**, which it previously could not detect
    at all. Expect a first run that applies index and foreign key changes it had been silently ignoring —
    that is the backlog being worked off, not new drift.
  - **SQLite no longer drops the table to change a column.** A change that `ALTER TABLE` cannot express
    used to be answered by dropping the object and recreating it, losing every row.
  - **Column names are no longer case-folded or rewritten.** Wolverine's NServiceBus PostgreSQL queue
    table declared PascalCase columns and relied on Weasel folding them to lowercase; a real
    NServiceBus-provisioned table has lowercase columns, so the declaration was wrong all along and is now
    stated as lowercase directly. No behavior change against an NServiceBus-owned table.
  - **Oracle table introspection is now six queries rather than one**, which is what made the Oracle
    command-builder bug above reachable.

### WolverineFx (core)

- **`AfterCommit` — a declarative way to run work after the transactional commit.**
  ([#3976](https://github.com/JasperFx/wolverine/pull/3976), closes
  [#3975](https://github.com/JasperFx/wolverine/issues/3975)) `After` reads like a post-handler hook that
  runs at the end. It does not run after the commit. The commit is itself a postprocessor contributed by
  the persistence provider, and `After` methods are inserted at the **front** of that list — so an `After`
  method observing a write is observing one that is not durable yet and may still roll back. There was no
  supported way to ask for the other side of it.

  ```csharp
  public static class RaiseAlertHandler
  {
      public static void Handle(RaiseAlert command, IDocumentSession session)
          => session.Events.Append(command.Id, new AlertRaised(command.Reason));

      // Only runs if the append above actually committed
      public static void AfterCommit(AlertLatch latch, RaiseAlert command)
          => latch.MarkRaised(command.Id);
  }
  ```

  Use the `AfterCommit` / `AfterCommitAsync` convention or `[WolverineAfterCommit]`, on message handlers,
  sagas and HTTP endpoints. Parameters bind exactly as `After` already does.

  The position is **structural**, not positional: frames go into a new `IChain.PostCommitPostprocessors`
  list concatenated after every postprocessor at frame-assembly time, rather than being appended from a
  policy sequenced after the persistence policy. Getting the position right by luck of policy ordering is
  exactly what silently breaks later.

  Two behaviours worth knowing: they **do not run when the commit throws** (frames are concatenated without
  a `try`/`finally`, so the exception unwinds past them), and they run after the outbox flush, so a message
  cascaded from one is not atomic with the write. `After`'s pre-commit position is unchanged.

  Verified per provider — Marten, Polecat, Fisher, EF Core, RavenDb and CosmosDb each have a codegen test
  asserting the emitted call lands after that provider's own commit frame.

- **A store-agnostic `EventsToAppend` return type.**
  ([#3969](https://github.com/JasperFx/wolverine/pull/3969), closes
  [#3941](https://github.com/JasperFx/wolverine/issues/3941)) `Wolverine.Marten.Events`,
  `Wolverine.Polecat.Events` and `Wolverine.Fisher.Events` are identical but store-named, so a handler that
  wanted to be store-agnostic could not name any of them. The store-agnostic path did exist — a bare
  `IEnumerable<object>` return is picked up by a fallback — but that fallback is **positional**:
  `IEnumerable<T>` is covariant, so every reference-typed collection in a return tuple is a candidate and
  the first one wins. Nothing failed at codegen or at runtime; the wrong collection simply became the
  appended events.

  Note the type is `EventsToAppend`, not `Events`. Naming it `Events` would have been a source-breaking
  collision (`CS0104`) for any handler importing both the core event-sourcing namespace and a store
  integration — that is, on the very declaration the feature exists for.

- **Ask which message types will be handled, and how a batch is shaped.**
  ([#3977](https://github.com/JasperFx/wolverine/pull/3977), closes
  [#3974](https://github.com/JasperFx/wolverine/issues/3974)) Discovery materializes after options time, so
  an extension installing *fallback* handlers could not ask "will this message type have a handler?" and had
  to hand-roll a mirror of Wolverine's own discovery convention. Such a mirror drifts silently — one that
  scanned a single assembly stopped seeing handlers that moved to a second, and installed a bare relay
  **over** a real handler.

  ```csharp
  opts.OnHandlersDiscovered(handlers =>
  {
      if (!handlers.Handles<ServiceUpdates>())
      {
          // safe to install a fallback
      }
  });
  ```

  Separately, `IMessageBatcher.BatchMessageType` is a free-form `Type` — a custom batcher need not produce
  `T[]` — so consumers were inferring the handled type from array-ness, which is wrong for exactly those
  batchers. `WolverineOptions.TryFindBatchMessageType(elementType, out var batchMessageType)` and
  `WolverineOptions.BatchMappings` now expose the real mapping.

- **Startup says so when a batch cannot be sequenced against its unbatched siblings.**
  ([#3978](https://github.com/JasperFx/wolverine/pull/3978), closes
  [#3973](https://github.com/JasperFx/wolverine/issues/3973)) Following up
  [#3867](https://github.com/JasperFx/wolverine/issues/3867): a batched element type that also has unbatched
  handlers has two independent execution paths writing the same entity — the assembled batch on its own
  local queue, and the unbatched siblings inside the listener's own execution block. A partitioned topology
  resolves that; without one, nothing does, and `Sequential()` on the batch queue does not close it (it
  serializes the batch against itself only).

  The asymmetry is the hazard: with a `GlobalPartitioned` topology the configuration is safe, and without
  one — embedded hosts, single-node deployments, most test fixtures — the same code has two concurrent
  writers and surfaces as intermittent stream-version collisions under load. Wolverine now warns at startup
  naming the message type, the queue, the fix and the wrong fix. `opts.AssertBatchExecutionIsSequenced()`
  escalates it to a startup failure.

### WolverineFx.SignalR

- **Coalesce outgoing messages into one envelope per destination.**
  ([#3979](https://github.com/JasperFx/wolverine/pull/3979), closes
  [#3972](https://github.com/JasperFx/wolverine/issues/3972)) The transport had no batching or buffering of
  any kind, and Wolverine offers no sender-side hook for it — so an application that wanted it had to route
  outbound messages through a **local queue**, which makes that queue a cascade target for its own handlers.
  A handler forwarding with `SendAsync` then re-sends onto the queue it was read from.

  ```csharp
  opts.PublishAllMessages().ToSignalR()
      .CoalesceOutgoing(o =>
      {
          o.FlushInterval = 100.Milliseconds();
          o.MaxBatchSize  = 200;
      });
  ```

  Nothing round-trips a queue, so there is no queue to re-enter, and the buffer sits **after** the outbox
  rather than before it — which removes the "never use it for a message that tells the client to go and read
  something" caveat an application-level accumulator carries.

  Buffers are keyed by destination, so a message bound for one connection is never coalesced with one bound
  for another. Batches carry the individual CloudEvents documents in arrival order — each item keeps its own
  message type, since the CloudEvents envelope is per-outer-message — and are delivered on a dedicated
  **`ReceiveCoalescedMessages`** client operation. A batch holding a single message goes out on the normal
  operation, so the trickle case needs no client change. Anything still buffered is flushed at shutdown.

  ⚠️ Browser clients must handle `ReceiveCoalescedMessages` to receive coalesced batches; see the
  [SignalR guide](/guide/messaging/transports/signalr) for the unwrap snippet. Wolverine's own SignalR client
  transport handles it automatically.

### WolverineFx (core) + event store integrations

- **A handler can take the store agnostic `JasperFx.Events.Documents` contracts as parameters.**
  ([#3962](https://github.com/JasperFx/wolverine/pull/3962), closes
  [#3956](https://github.com/JasperFx/wolverine/issues/3956)) `IDocumentSessionOperations`,
  `IDocumentWriteOperations` and `IDocumentReadOperations` are the document side counterparts to the
  `IEventOperations` contracts Wolverine already understood, and they are the only way store agnostic
  source can take a session without naming a concrete store type. They now bind and commit on Marten,
  Polecat and Fisher alike:

  ```csharp
  // Valid against all three stores -- nothing Marten specific is named
  public static void Handle(RecordNote command, IDocumentSessionOperations session)
      => session.Store(new Note { Id = command.Id, Text = command.Text });
  ```

  Two gaps had to close. Codegen matches a variable by its exact type, so nothing satisfied the
  parameter from the `IDocumentSession` the chain had already created; and `CanApply` matched against
  a fixed type list naming none of them, so `AutoApplyTransactions` skipped the chain and no
  `SaveChangesAsync` postprocessor was attached. On a stock host the first gap surfaced as an outright
  `UnResolvableVariableException`; with only the second remaining, the handler ran and its writes were
  queued into the session's unit of work and **silently discarded**.

  All three contracts resolve from the chain's single `IDocumentSession`, including the read only one,
  so a handler taking the read and write contracts together cannot end up with two sessions whose reads
  miss its own pending writes. `IDocumentReadOperations` is deliberately not treated as evidence that a
  chain writes anything, exactly as `IQuerySession` never has been. The contracts are also registered in
  DI — the stores register only their own interfaces — delegating to `IDocumentSession`/`IQuerySession`
  so a service located contract still gets the handler's outbox enrolled session.

  ⚠️ Importing the `JasperFx.Events.Documents` **namespace** makes `ToListAsync()` ambiguous between
  `DocumentQueryableExtensions` and each store's own queryable extensions (`CS0121`). Alias the
  individual contracts instead of importing the namespace.

- **The persistence provider decides who owns a chain's transaction.**
  ([#3957](https://github.com/JasperFx/wolverine/pull/3957), closes
  [#3953](https://github.com/JasperFx/wolverine/issues/3953)) Ancillary store inference scanned
  `chain.ServiceDependencies()` for ancillary marker types, and `ServiceDependencies` walks constructor
  graphs **recursively** — so any dependency that merely *held* an ancillary store matched. A read only
  `Directory(ISystemStore)` two hops down counted the same as an injected `DbContext`, and a tenant
  Marten handler had its inbox and dead letters stolen by the wrong store. That inference was only ever
  correct for EF Core, where the injected `DbContext` genuinely is the transaction owner; Marten, Polecat
  and Fisher require `[MartenStore]`/`[Storage]`, which already populates `chain.AncillaryStoreType`.
  There is a new default null `IPersistenceFrameProvider.TryDetermineTransactionOwnerType` for this,
  implemented only by EF Core.

### WolverineFx (core)

- **Durability agents are no longer assigned to nodes that cannot run them.**
  ([#3963](https://github.com/JasperFx/wolverine/pull/3963), closes
  [#3954](https://github.com/JasperFx/wolverine/issues/3954)) A node started with
  `Durability.DurabilityAgentEnabled = false` never registers the durability agent family, so it threw
  `ArgumentOutOfRangeException: Unrecognized agent scheme 'wolverinedb'` the moment the leader handed it
  one. The leader then re-issued the identical assignment every five minutes indefinitely, no durability
  agent ran anywhere for that store, and `owner_id = 0` outgoing envelopes were never recovered. The
  failure was silent in both directions: every queue table read zero while the backlog grew.

  Nodes now publish a marker capability when the family is actually registered, and the leader skips
  nodes that have not. When no node in the cluster is capable, nothing is assigned and a warning names
  the condition and the setting rather than leaving another quiet failure.

  Note this is a **per node** capability rather than the per agent matching the blue/green and group
  affinity paths use. `AllNodesHaveSameCapabilities` returns true trivially both for a single node and
  when every node is equally incapable — precisely the two configurations reported — and the durability
  family's agent list grows at runtime as tenant databases are added, so per agent matching would strand
  every later added tenant's agent.

- **The idle reaper no longer permanently latches durable endpoints.**
  ([#3958](https://github.com/JasperFx/wolverine/pull/3958), closes
  [#3955](https://github.com/JasperFx/wolverine/issues/3955)) Two independent defects.
  `Endpoint.AutoStartSendingAgent()` is `UsedInShardedTopology || Subscriptions.Any()`, so a durable
  endpoint reached only via `EndpointFor(uri)` looked as disposable as an ephemeral reply queue and was
  reaped. And `RabbitMqEndpoint.ResolveSender` was a permanent `_sender ??=` cache, so the rebuilt agent
  wrapped a *disposed* sender and latched forever; `RemoveSendingAgentAsync` also left `Endpoint.Agent`
  pointing at the disposed agent, which is transport agnostic. `SendingAgentIdleTimeout` had no test
  coverage at all before this.

### WolverineFx.RabbitMQ

- **Opt in to waiting for prefetched messages when a listener drains.**
  ([#3796](https://github.com/JasperFx/wolverine/pull/3796)) Contributed by
  [@benjamin-alexander-simplisafe](https://github.com/benjamin-alexander-simplisafe). A listener can now
  be configured to wait for already prefetched messages to be processed as it stops, rather than letting
  the broker requeue them:

  ```csharp
  opts.ListenToRabbitQueue("orders").DrainWaitForPrefetch();
  ```

- **The prefetch drain is safe for a non terminal stop.**
  ([#3960](https://github.com/JasperFx/wolverine/pull/3960)) `RabbitMqListener.StopAsync` is not always
  terminal — `RequeueContinuation` stops the listener inline from the handler pipeline and the background
  `PauseAsync` stops the same consumer again. A JasperFx `BatchingChannel` tolerates every post completion
  call silently, which means `PostAsync` after `Complete` **discards the envelope with no exception**, so a
  delivery landing between the drain's `Complete()` and the dispose latch vanished and was redelivered.
  The latch now happens before completing.

- **A rejected settle quiesces the channel the broker has already closed.**
  ([#3964](https://github.com/JasperFx/wolverine/pull/3964), addresses
  [#3950](https://github.com/JasperFx/wolverine/issues/3950)) When the broker rejects a settle with
  `PRECONDITION_FAILED - unknown delivery tag` it has already closed that channel, and Wolverine carried
  on delivering into it. A channel torn down with deliveries still in flight is what makes RabbitMQ.Client
  race itself — `SessionManager.Lookup` throws `KeyNotFoundException` for the channel number it just
  removed and the client escalates that into a library initiated close of the **entire connection**
  (`code=541`), taking down every listener and sender on it. Wolverine now cancels that channel's consumer
  and rebuilds instead of feeding a channel known to be dead.

  This narrows the window and speeds recovery; it does **not** prevent the connection close, which is set
  in motion before Wolverine can observe anything — the exception is raised on the client's own `MainLoop`
  after the ack has gone out. The underlying defect is upstream in rabbitmq-dotnet-client, where
  `SessionManager.Lookup` should `TryGetValue` and drop frames for a dead channel rather than throwing.

- **Listener recovery after a mid flight connection death is now asserted.**
  ([#3961](https://github.com/JasperFx/wolverine/pull/3961), covers
  [#3950](https://github.com/JasperFx/wolverine/issues/3950)) That Wolverine recovers from the connection
  close above was observed but never tested. A test now kills the broker connection through the management
  API and asserts a message published *after* the kill still arrives. Two things it encodes: `/api/connections`
  lags the TCP connect on RabbitMQ 4.x (empty at 4s, populated at 8s, on a healthy host passing traffic), and
  the kill is scoped by a per test `ClientProvidedName` so it cannot take down a concurrently running test's
  connections.

### WolverineFx.Http.Fisher (new package)

- **New `WolverineFx.Http.Fisher` package.** ([#3949](https://github.com/JasperFx/wolverine/pull/3949),
  closes [#3944](https://github.com/JasperFx/wolverine/issues/3944)) There was a
  `Wolverine.Http.Marten` and a `Wolverine.Http.Polecat` and no Fisher equivalent, so a Fisher-backed
  application had nothing to reference for the aggregate/document HTTP attributes. The third flavour
  now exists alongside its siblings.

### WolverineFx.Sqlite / WolverineFx.Fisher

- **A SQLite "schema name" is now the table name prefix it was always documented to be.**
  ([#3945](https://github.com/JasperFx/wolverine/pull/3945), closes
  [#3943](https://github.com/JasperFx/wolverine/issues/3943)) Setting
  `FisherIntegration.MessageStorageSchemaName`, or the `schemaName` argument to
  `PersistMessagesWithSqlite()`, used to reach the message store as a `schema.table` qualifier.
  SQLite has no user-defined schemas — the only names a plain connection knows are `main`, `temp`,
  and whatever has been `ATTACH`ed — so any value other than `main` emitted SQL against a database
  that never existed and the host died on the first envelope write with
  `no such table: <name>.wolverine_incoming_envelopes`.

  The two halves had disagreed all along: Weasel's `SqliteObjectName` drops the schema from its
  qualified name, so the DDL had been creating a bare `wolverine_incoming_envelopes` while the
  inherited DML asked for a qualified one. The default `main` is the only thing that hid it.

  The name is now folded into the table names as a prefix, which is a meaning SQLite can honour —
  several logically separate Wolverine table sets inside one database file:

  ```csharp
  opts.PersistMessagesWithSqlite(connectionString, "reporting");
  // => reporting_wolverine_incoming_envelopes, reporting_wolverine_outgoing_envelopes, ...
  ```

  This takes in the envelope, node, control queue, tenant, listener and saga tables, plus the
  dead-letter index names (SQLite shares one identifier namespace between tables and indexes).
  **`main` prefixes nothing, so databases provisioned before this release are untouched and there is
  no migration.** Postgres, SQL Server, MySQL and Oracle render exactly as before.

  `FisherIntegration.TransportSchemaName` is now documented as what it has always been on a Fisher
  host: inert. Tracked in [#3947](https://github.com/JasperFx/wolverine/issues/3947).

- **Fisher 0.7.0.** ([#3946](https://github.com/JasperFx/wolverine/pull/3946)) The package floor moves
  from 0.6.0 to 0.7.0. Note that Fisher 0.7.0 bundles `JasperFx.Events.SourceGenerator` inside its own
  nupkg, as Polecat already does — a project that also references that generator explicitly will get
  two analyzer instances and a `CS0433` duplicate-type error until one copy is removed.

### WolverineFx.Polecat

- **`Nullable<T>` is unwrapped when determining a Polecat aggregate's id type.**
  ([#3948](https://github.com/JasperFx/wolverine/pull/3948), closes
  [#3942](https://github.com/JasperFx/wolverine/issues/3942)) Marten and Polecat disagreed for an
  aggregate whose id property is nullable: Polecat answered `Nullable<T>` verbatim, which is not a
  primitive id type, so the documented `IdentifiedBy<T>` escape hatch was skipped entirely. The two
  stores now agree.

### WolverineFx (core) + event store integrations

- **New store agnostic `Storage.AppendEvents()` and `Storage.StartStream()` side effects.**
  ([#3934](https://github.com/JasperFx/wolverine/pull/3934)) `Storage.Store()` and friends write
  documents; these are their counterparts for an event stream, returned from a handler or HTTP
  endpoint the same way. The work is expressed entirely against `JasperFx.Events.IEventOperations`,
  the shared write-side API Marten, Polecat and Fisher all implement, so the same handler is valid on
  any of them and needs no `IDocumentSession`:

  ```csharp
  public static AppendEvents Handle(ApproveInvoice command)
      => Storage.AppendEvents(command.Id, new InvoiceApproved(command.ApprovedBy));
  ```

  Streams may be identified by `Guid` or string key, `AppendEvents` accepts an optional
  `expectedVersion` for optimistic concurrency, and an `AppendEvents` carrying no events is a
  deliberate no-op so a decision function may conclude that nothing happened. Wolverine enrolls the
  chain in the event store's transaction, so the events commit together with any outgoing messages
  through the outbox. Handlers marked `[Storage(typeof(IMyStore))]` append to that ancillary store.

- **New `[FirstOrDefault]` attribute for storage agnostic reads.**
  ([#3933](https://github.com/JasperFx/wolverine/pull/3933)) `[Entity]` needs an identity to load by,
  so it cannot express the singleton document — a type a system stores exactly one of, looked up by
  nothing at all. `[FirstOrDefault]` resolves the equivalent of
  `session.Query<T>().FirstOrDefaultAsync()` through whichever provider owns the type:

  ```csharp
  public static MetricsAlertDefaults Get([FirstOrDefault] MetricsAlertDefaults? defaults)
      => defaults ?? new MetricsAlertDefaults();
  ```

  Supported by Marten, Polecat, Fisher, RavenDb and EF Core. The parameter is simply `null` when
  nothing matches and the handler runs anyway, so there is deliberately no `Required` / `OnMissing`.
  **CosmosDb is not supported** — its integration stores every user document in one shared container
  with no per-type discriminator, so "the first document of type `T`" cannot be asked for safely; a
  `[FirstOrDefault]` on a CosmosDb-persisted type fails at bootstrapping time rather than returning
  the wrong object.

- **New `OnMissing.EmptyContentWith204`.**
  ([#3931](https://github.com/JasperFx/wolverine/pull/3931)) Answers an empty **204** instead of the
  default 404 when a required entity cannot be loaded, for callers who would rather say "the Url is
  correct, but there is no body." Reaches every attribute that loads data this way — `[Entity]`,
  `[Document]`, `[Aggregate]`, `[ReadAggregate]`, `[WriteAggregate]`, `[ReadModel]`, `[WriteModel]`
  and the DCB attributes — and is settable globally through `WolverineOptions.EntityDefaults.OnMissing`.
  On a `GET` or `QUERY` endpoint it also forces the data to be treated as required, since running the
  endpoint with a null entity to return an empty body anyway buys nothing.

- **New `[All]` and `[Queryable]` parameter attributes.**
  ([#3936](https://github.com/JasperFx/wolverine/pull/3936)) `[All]` supplies every document of its
  element type — the equivalent of `session.Query<T>().ToListAsync()` — as an `IReadOnlyList<T>`,
  resolved through whichever provider owns the type:

  ```csharp
  public static IReadOnlyList<ServiceAlertOverrides> GetAll([All] IReadOnlyList<ServiceAlertOverrides> overrides)
      => overrides;
  ```

  Supported by Marten, Polecat, Fisher, RavenDb and EF Core; **CosmosDb is not supported**, for the same
  reason as `[FirstOrDefault]`.

  On **Marten, Polecat and Fisher, two or more batchable reads in the same handler now resolve in a single
  database round trip** rather than one query each ([#3938](https://github.com/JasperFx/wolverine/pull/3938)).
  An `[All]` joins the same batch as an `[Entity]` load or a query specification. A lone read is
  deliberately left standalone.

  `[Queryable]` injects the store's own `IQueryable<T>` for the cases the other attributes do not cover. It
  is an escape hatch and the documentation says so at length — it is **not portable in practice** even
  though the type is, since LINQ provider capabilities differ sharply between stores. Marten 9 refuses
  synchronous LINQ outright, so a `.ToArray()` that works on EF Core throws at runtime on Marten: **always
  use the async operators.** All six providers, CosmosDb included, with a warning that its shared container
  can surface other document types.

- **A handler or endpoint parameter of `IEventStoreOperations` / `IEventOperations` now resolves — and
  commits.** ([#3936](https://github.com/JasperFx/wolverine/pull/3936)) Two defects: no variable source
  matched the shared `JasperFx.Events` contracts (each store registered only its own derived spelling), and
  more seriously, `CanApply` did not recognize **any** event operations type, so `AutoApplyTransactions`
  skipped those chains entirely and appended events were queued into the session's unit of work and never
  committed — with no exception. That second one also affected each store's *own* event operations types,
  so it predates this release.

- **`[All]`, `[Queryable]` and `[FirstOrDefault]` provider errors now name the declaring method.**
  ([#3937](https://github.com/JasperFx/wolverine/issues/3937)) These attributes validate at codegen, so the
  failure can land on a chain you did not know was being compiled — an assembly carrying
  `[assembly: WolverineModule]` puts every endpoint in it into discovery. The message named the parameter
  and its element type but nothing you would recognise; it now ends with
  `on MyApp.Endpoints.AlertConfigHistoryEndpoint.GetConfigHistory() cannot be resolved.`

### WolverineFx.Http

- **New `[NoContentIfMissing]` / `[NotFoundIfMissing]` attributes and
  `WolverineHttpOptions.OnMissingResponseBody`.**
  ([#3931](https://github.com/JasperFx/wolverine/pull/3931)) Control whether a **null response body**
  is written as the default 404 or as an empty 204, per endpoint method, per endpoint class, or
  application wide. The generated OpenAPI follows. `[NoContentIfMissing]` is only legal on `GET` and
  `QUERY` endpoints and fails at bootstrapping time otherwise, and the application wide setting
  likewise stops at those verbs — a 204 in place of a resource on a `POST` would turn a failed
  command into an apparent success for the caller.

- **A `DateTime now` or `DateTimeOffset now` endpoint parameter is now filled with the current UTC
  time**, matching the long standing message handler convention.
  ([#3932](https://github.com/JasperFx/wolverine/pull/3932)) Previously such a parameter silently
  bound from the query string instead, so the endpoint received `default` on any request that did not
  happen to pass `?now=`. The value comes from the same `IVariableSource` message handlers use, so a
  custom clock registered there is honored by both. The convention is keyed on the parameter being
  *named* `now`; an ordinary `DateTimeOffset from` / `to` query parameter is unaffected, and an
  explicit `{now}` route argument or `[FromQuery]` / `[FromRoute]` / `[FromHeader]` still wins.

- **A null `string` resource returns 404 rather than throwing.** Previously
  `HttpHandler.WriteString` dereferenced the null while setting `ContentLength`, so a
  `string`-returning endpoint answered **500** where every other resource type answered 404.

### WolverineFx (core) + event store integrations

- **`[WriteAggregate]` keeps its original `Required` default — regression fix.**
  ([#3929](https://github.com/JasperFx/wolverine/issues/3929)) `WriteAggregateAttribute` derives from
  `WriteModelAttribute` and overrides neither `Modify` nor `Required`, so it inherited GH-3916's
  nullability inference in 6.27.0. `[WriteAggregate]` shipped a year before that inference, so an
  existing `[WriteAggregate] Account? account` handler silently lost its not-found guard and began
  running against a model that was never loaded. `[WriteAggregate]` and `[ReadAggregate]` now pin the
  unconditional `Required = true` they have always had, in Marten, Polecat and Fisher alike. Say
  `Required = false` explicitly, or use `[WriteModel]` / `[ReadModel]`, to opt out.

- **`[ReadModel]` now takes `Required` from the parameter's nullable annotation**, matching what
  GH-3916 did for `[WriteModel]`: `Order order` is required, `Order? order` is not and is handed to
  the method as `null`. An explicit `Required` at the call site still wins. `[Entity]`,
  `[DeciderFunction]` and `[DcbModel]` are unchanged.

  Note that in an assembly compiled with `<Nullable>disable</Nullable>` the annotation reads as
  *unknown* rather than *nullable*, so `[WriteModel]` and `[ReadModel]` fall back to `Required = true`
  there — the inference is a no-op for those projects rather than a silent behaviour change.


### WolverineFx.AmazonSqs

- **An oversized message is no longer retried forever, and can optionally be fragmented.**
  ([#3926](https://github.com/JasperFx/wolverine/issues/3926)) SQS caps a message at 256KB and
  rejects a larger one with `InvalidParameterValue - Message must be shorter than 262144 bytes
  (SenderFault: true)`. `SenderFault: true` means the identical request will fail identically
  forever, but Wolverine treated it as a transient send failure and re-queued it — which is why
  this presented as a *flood* of identical errors rather than one. An oversized message is now
  logged once and discarded.

  New opt-in `FragmentOversizedMessages()` on the listener and subscriber configurations splits an
  oversized body across several SQS messages using Wolverine's own framing in SQS message
  attributes, and reassembles it on the receiving side. **[Claim
  checks](https://wolverinefx.net/guide/durability/claim-checks) remain the recommended answer** —
  `WolverineFx.ClaimCheck.AmazonS3` is the AWS-sanctioned pattern and has none of the constraints
  below. Reassembly is in memory on a single listener, so fragmentation is only safe on a FIFO
  queue, behind a globally partitioned listener, or with a single listening node. Nothing is
  acknowledged until a fragment set is complete, so a node that crashes holding part of one loses
  nothing. See [Large Messages in
  SQS](https://wolverinefx.net/guide/messaging/transports/sqs/large-messages).

  A receiving endpoint now always asks SQS for the fragment attribute names in `ReceiveMessage`,
  appended to whatever `MessageAttributeNames` the endpoint already requested (`"All"` is left
  alone). SQS returns only the attributes a receive explicitly names.

### WolverineFx (core)

- **A permanently unsendable envelope is now deleted from the outgoing table.**
  ([#3926](https://github.com/JasperFx/wolverine/issues/3926)) `SendingAgent.MarkSerializationFailureAsync`
  — the path a transport uses to say "this envelope can never be sent" — only logged. On a durable
  sending endpoint the row stayed in the outgoing table, so the durability agent re-read and
  re-sent it on every recovery sweep. It is now `virtual`, and `DurableSendingAgent` overrides it
  to delete the rows.

### WolverineFx.ComplianceTests

> ⚠️ DRAFT WORDING — needs Jeremy's review before release.

- **Now built on xUnit v3, and requires consumers to be on xUnit v3.** The compliance suites are
  base classes you inherit, and an xUnit v2 test project cannot inherit from an xUnit v3 base
  class — so this is a hard break with no side-by-side option. If you maintain a community
  transport or persistence provider and are not ready to move, **pin
  `WolverineFx.ComplianceTests` to 6.24.x** and stay there until you migrate; the 6.24.x package
  continues to work against xUnit v2 exactly as before.

  To move: reference `xunit.v3` instead of `xunit`, add `<OutputType>Exe</OutputType>` to your
  test project (xUnit v3 test projects are executables and its targets hard-error without it),
  and change your `IAsyncLifetime` implementations from `Task` to `ValueTask` — in v3
  `IAsyncLifetime` derives from `IAsyncDisposable`, so `DisposeAsync` returns `ValueTask` too.
  `ITestOutputHelper` also moved from `Xunit.Abstractions` to `Xunit`. The xUnit team's
  [v3 migration guide](https://xunit.net/docs/getting-started/v3/migration) covers the rest.

  The package itself is unchanged in shape: it is still a plain library (it references
  `xunit.v3.extensibility.core`, not `xunit.v3`, so it stays a DLL rather than becoming an
  executable) and the base classes, their names, and their test methods are all as they were.

### WolverineFx.Oracle

- **Durable inbox no longer fails on Oracle with "Value does not fall within the expected range".**
  ([#3581](https://github.com/JasperFx/wolverine/issues/3581)) With `.UseDurableInbox()` on an Oracle
  store, `EfCoreEnvelopeTransaction.CommitAsync` marked the already-persisted envelope handled by binding
  its `Guid` id through Weasel's generic path, which sets `DbType.Guid` — something ODP.NET rejects against
  the `RAW(16)` id columns, rolling back the whole commit even though the message handled successfully. The
  mark-as-handled update now routes through a new provider-aware
  `IMessageDatabase.MarkIncomingEnvelopeAsHandledInTransactionAsync` (a default interface method preserving
  the existing generic binding for every `DbType.Guid`-friendly provider) that Oracle overrides to bind the
  Guid as `byte[]`, exactly as its other inbox writes already do. Runs inside the application's own EF Core
  transaction. Thanks to adityaBisht2304 for the detailed diagnosis.

### WolverineFx (core)

- **New `IHost.ClearAllWolverineStorageAsync()`; message-store resets stay envelope-storage only.**
  ([#3592](https://github.com/JasperFx/wolverine/issues/3592)) Reverses the unreleased wave that made
  `IMessageStoreAdmin.ClearAllAsync()` / `RebuildAsync()` also truncate the tables owned by a
  database-backed queue transport (PRs [#3529](https://github.com/JasperFx/wolverine/pull/3529),
  [#3555](https://github.com/JasperFx/wolverine/pull/3555), [#3557](https://github.com/JasperFx/wolverine/pull/3557),
  [#3558](https://github.com/JasperFx/wolverine/pull/3558), [#3559](https://github.com/JasperFx/wolverine/pull/3559),
  all merged after 6.21.0 — so nothing shipped with the widened semantics). Silently widening a
  long-standing "envelope storage" API to also destroy transport data was surprising, and the right
  scope is genuinely ambiguous per provider: SQL Server's rate-limit table is registered through the
  same `AddTable` path but must survive a reset. The per-provider `truncateAdditionalTablesAsync` hook
  is gone; the neighboring `afterTruncateEnvelopeDataAsync` hook is unrelated and unchanged.

  Replacing it is an explicit, opt-in test-support extension: `IHost.ClearAllWolverineStorageAsync()`
  rebuilds envelope storage for every known message store (main, every tenant database, every ancillary
  store) *and* leaves every database-backed queue transport's tables built but empty, fanning out across
  tenant databases. It is built on the uniform `IBrokerQueue.SetupAsync()` / `PurgeAsync()` endpoint API,
  so it covers PostgreSQL, SQL Server, MySQL, Oracle, SQLite and Redis streams with no provider-specific
  code — including SQL Server, whose queue tables are not registered on the message store and which the
  reverted approach could never reach (closes [#3554](https://github.com/JasperFx/wolverine/issues/3554)).
  Safe to call on hosts with no message store and no database queues. See the
  [testing guide](https://wolverinefx.net/guide/testing.html#resetting-all-wolverine-storage-in-tests).

  Also: `SetupAsync()` on the five relational queue transports no longer short-circuits on its
  "already checked this database" memo. It is the explicit "make sure these tables exist right now"
  call, so it has to re-apply against a database whose queue tables were dropped after the first check.
- **Fixed: durable exclusive / leader-pinned listeners never recovered their dormant inbox messages when the
  durability agent ran on another node ([#3590](https://github.com/JasperFx/wolverine/issues/3590)).**
  Inbox recovery was gated on the *local* listener circuit being `Accepting`, but the `DurabilityAgent` is
  assigned per message database and distributed independently of the listener agents. Whenever the agent for a
  database landed on a node that was not hosting that endpoint's exclusive listener, messages sitting at
  `owner_id = 0` (the state an ungraceful shutdown leaves behind) were never recovered — a permanent deadlock
  between two independently-assigned agents. Now the per-database durability agents skip every destination whose
  `ListenerScope` is not `CompetingConsumers` (RDBMS, RavenDb and CosmosDb agents alike), and the node actually
  hosting the listener recovers them itself through the new `ListenerInboxRecovery`: an initial sweep when the
  listener reaches `Accepting`, then polling on the `Durability.ScheduledJobPollingTime` cadence for as long as
  it stays `Accepting`. The sweep covers the main store, every tenant database in a separate-database-per-tenant
  system, and every ancillary store, and it respects latching and `BufferingLimits` exactly as before. Applies
  to `ExclusiveNodeWithParallelism()`, `ListenWithStrictOrdering()`, and `ListenOnlyAtLeader()`, in `Solo` mode
  as well as `Balanced`. Also adds `IEndpointCollection.IsSingleNodeListener(Uri)` (a default interface method,
  so existing implementors are unaffected) and a reusable `ExclusiveListenerRecoveryCompliance` fixture in
  `WolverineFx.ComplianceTests`. See the
  [exclusive node processing guide](https://wolverinefx.net/guide/messaging/exclusive-node-processing.html#inbox-recovery-ownership).

- **New `IMessageBus.StreamAsync<TRequest, TResponse>` primitive for streaming requests.**
  The mirror image of `StreamAsync<T>`: a caller hands one handler invocation an
  `IAsyncEnumerable<TRequest>` stream of messages and awaits a single `TResponse`. The handler
  declares `IAsyncEnumerable<TRequest>` as its message type
  (`Task<TResponse> Handle(IAsyncEnumerable<TRequest> messages, CancellationToken token)`) and
  consumes the stream incrementally — nothing is materialized by the framework. Local invocation
  only; a missing handler fails fast with a `NotSupportedException` naming the expected signature.
  Cascading messages and `DeliveryOptions` work as with any invoked handler. See the
  [message bus guide](https://wolverinefx.net/guide/messaging/message-bus.html#streaming-requests).
  Note: this adds two members to `ICommandBus`, which is source-breaking for custom
  `IMessageBus`/`ICommandBus` implementors (same precedent as the original `StreamAsync` addition).

- **`resources setup` now provisions message storage even when `AutoCreate` is `None`.**
  ([#3573](https://github.com/JasperFx/wolverine/issues/3573)) The documented production recipe of
  `ResourceAutoCreate = AutoCreate.None` plus an explicit `resources setup` / `IHost.SetupResources()`
  deployment step silently skipped the `wolverine.*` schema migration. An explicit setup call is now
  treated as intent to provision: `MessageStoreResource.Setup` migrates with `CreateOrUpdate`
  regardless of the configured `AutoCreate` (`CreateOrUpdate` never drops data). Passive paths —
  host startup and tenant store discovery — still honor `AutoCreate.None`, but the previously silent
  skips now log: a warning when a schema difference is detected at runtime under `AutoCreate.None`,
  and informational messages when startup or tenant-discovery migration is skipped. New public
  surface: `IMessageStoreAdmin.MigrateAsync(AutoCreate? overrideAutoCreate)` default-interface
  overload (defaults to the parameterless `MigrateAsync()`, so external store implementations are
  unaffected). Thanks to Laurence Gillian!

### WolverineFx.Grpc

- **Proto-first client-streaming RPCs (`stream TRequest → TResponse`) are now code-generated.**
  A `[WolverineGrpcService]` stub declaring the fourth canonical gRPC shape no longer fails fast at
  startup — Wolverine generates a wrapper that adapts the inbound `IAsyncStreamReader<TRequest>` to
  `IAsyncEnumerable<TRequest>` and forwards it to the new `IMessageBus.StreamAsync` for a
  single response. Tenant-id detection applies to client-streaming methods; before/after middleware
  and the `Validate` convention are not woven (same constraint as bidirectional streaming). The
  server-side exception interceptor now also translates exceptions from client-streaming handlers
  per AIP-193, and `IGrpcEndpointManifest` surfaces the new `GrpcRpcStreamKind.ClientStreaming`
  descriptors. See the [gRPC streaming guide](https://wolverinefx.net/guide/grpc/streaming.html).

- **Code-first client-streaming RPCs are now code-generated too.** A `[WolverineGrpcService]`
  interface method shaped `Task<TResponse> Name(IAsyncEnumerable<TRequest>[, CallContext])` is no
  longer skipped — Wolverine generates an implementation forwarding the inbound stream (which
  protobuf-net.Grpc already exposes as `IAsyncEnumerable<TRequest>`, so no stream-reader adapter
  is involved) to `IMessageBus.StreamAsync<TRequest, TResponse>`. Tenant-id detection is woven
  when the method declares a `CallContext` parameter, giving parity with proto-first;
  before/after middleware and the `Validate` convention are not woven (same constraint as the
  other streaming shapes). `IGrpcEndpointManifest` surfaces code-first client-streaming
  descriptors with the per-item element type as the request. New public surface:
  `CodeFirstMethodKind.ClientStreaming` (appended enum member). The bidirectional code-first
  shape (`IAsyncEnumerable<TResponse>` return with a streamed request) remains hand-written only.
  See the [gRPC streaming guide](https://wolverinefx.net/guide/grpc/streaming.html).

### WolverineFx.Http

- **New `openapi` command for build-time OpenAPI generation without starting the host.**
  `dotnet run -- openapi` writes the application's OpenAPI document straight from endpoint metadata,
  reusing the same `Microsoft.AspNetCore.OpenApi` document provider that Microsoft's
  `GetDocument.Insider` tool uses, but **without** calling `IHost.StartAsync()`. This means the
  document can be generated in build/CI pipelines for applications backed by database message
  persistence (or external brokers) with no database or broker connectivity required. Requires
  `builder.Services.AddOpenApi()`. Writes to standard output by default; supports `--document`,
  `--output` (a file path), `--list`, and `--route` (a fuzzy route filter that emits only the matching
  paths and the schema components they reference — handy for troubleshooting a single endpoint). See the
  [HTTP metadata guide](https://wolverinefx.net/guide/http/metadata.html) (GH-2903).

## 6.0.1

Patch release on the 6.0 line: a Critter Stack dependency refresh plus two
targeted fixes and one new opt-in transport feature. No breaking changes.

### WolverineFx (core)

- **Keyed services now resolve correctly when code generation falls back to
  service location.** When a handler dependency injected `IServiceProvider`
  directly or used an opaque lambda registration (such as the ones the MS Graph
  SDK adds), the generated code dropped the service key and emitted
  `GetRequiredService<T>` instead of `GetRequiredKeyedService<T>`, throwing at
  runtime. Fixed upstream in the JasperFx 2.0.1 code generation
  (jasperfx GH-2878) and pulled in via the dependency bump below.

### WolverineFx.AmazonSqs

- **Amazon SQS standard queues can opt into [fair queues](https://docs.aws.amazon.com/AWSSimpleQueueService/latest/SQSDeveloperGuide/using-messagegroupid-property.html)**
  via `EnableFairQueueMessageGroups()`, which maps `Envelope.GroupId` to the SQS
  `MessageGroupId` on outgoing messages to improve fairness for multi-tenant
  workloads. Opt-in per endpoint, no ordering/deduplication semantics, and no
  effect on FIFO queues (which always map `MessageGroupId`). (#2886)

### WolverineFx.Marten

- **Ancillary store outbox honors the per-store envelope schema.** Projection
  side-effect messages published from an ancillary Marten store integrated via
  `IntegrateWithWolverine(x => x.SchemaName = ...)` on a separate database now
  write envelopes to that store's own schema instead of the main store's,
  fixing `42P01: relation "public.wolverine_incoming_envelopes" does not exist`
  in modular-monolith setups. (#2887)

### Dependencies

- JasperFx `2.0.0` → `2.0.1`
- JasperFx.Events (and `.Events.SourceGenerator`) `2.0.0` → `2.1.0`
- JasperFx source-generator package repointed to `JasperFx.SourceGenerator` `2.0.1` (#2891)
- Marten (and `.AspNetCore` / `.Newtonsoft`) `9.0.0` → `9.0.1`
- Polecat `4.0.0` → `4.1.1`
- Weasel.* (7 packages) `9.0.0` → `9.0.1`

## 6.0.0-alpha.1

First explicitly-versioned 6.0 alpha. Cumulative work since 5.39.0 on the `main`
branch. **See the [migration guide](https://wolverinefx.net/guide/migration.html#key-changes-in-6-0)
for the full breaking-change inventory and the at-a-glance table.**

### WolverineFx (core)

- **Dropped `net8.0` support.** Target frameworks are now `net9.0;net10.0`. The
  JasperFx 2.0-alpha line that 6.0 builds on no longer targets net8.0. (BREAKING)
- **Bumped the critter-stack dependency line** to `JasperFx 2.0.0-alpha.*`,
  `JasperFx.Events 2.0.0-alpha.*`, `Marten 9.0.0-alpha.*`, `Polecat 4.0.0-alpha.*`.
  (BREAKING)
- **`WolverineOptions.ServiceLocationPolicy` default flipped** from
  `AllowedButWarn` to `NotAllowed` (#2584). Apps that previously relied on
  Wolverine's code generation falling back to service location at runtime now
  throw `InvalidServiceLocationException` on startup. Restructure registrations
  or allow-list per type via `opts.CodeGeneration.AlwaysUseServiceLocationFor<T>()`.
  Soft-landing: `opts.RestoreV5Defaults()` flips this back. (BREAKING)
- **Pooled `Envelope` instances at the two `Executor.InvokeAsync` sites** for the
  internal receive pipeline (#2741 closing part of #2726). Allocation reduction
  on the hot path; no public API change. Gated on `ActiveSession == null` so
  tracking sessions, observer tests, and the `ITrackedSession.Events` capture-
  after-handler scenario all keep fresh allocations.
- **New `WolverineOptions.RestoreV5Defaults()`** one-line migration affordance.
  Restores every changed runtime default back to its 5.x value (today that means
  `ServiceLocationPolicy`; more lines get appended as additional defaults flip
  in 6.x patch releases).
- **Stale `DefaultSerializer` XmlDoc fixed.** The doc-comment had claimed
  Newtonsoft.Json was the default; STJ has actually been the default since
  Wolverine 5.0.
- **Performance: per-endpoint serializer cache pre-population** during
  `Endpoint.Compile()`. Hot-path serializer lookup is now a pure read.
- **`Subscription.Scope` JSON converter** swapped from Newtonsoft's
  `[StringEnumConverter]` to STJ's `[JsonStringEnumConverter]`. Wire format
  unchanged (still string-named scopes).

### WolverineFx.Newtonsoft (new package)

- **Extracted all Newtonsoft.Json integration** out of core `WolverineFx` into a
  new separate `WolverineFx.Newtonsoft` package (#2743). Core `WolverineFx`
  no longer depends on `Newtonsoft.Json`. The 5.x APIs (`UseNewtonsoftForSerialization`,
  `CustomNewtonsoftJsonSerialization`, `IMassTransitInterop.UseNewtonsoftForSerialization`,
  the `NewtonsoftSerializer` type) are now **extension methods** in the new
  package — same call shape, just need `dotnet add package WolverineFx.Newtonsoft`
  + `using Wolverine.Newtonsoft;`. (BREAKING)
- Transports that pin a `NewtonsoftSerializer` internally for NServiceBus /
  MassTransit wire-compat (RabbitMQ's `UseNServiceBusInterop()`, the AWS SQS
  and SNS NServiceBus mappers, Azure Service Bus listeners) carry the
  `WolverineFx.Newtonsoft` dependency on consumers' behalf.

### Namespace moves (BREAKING)

- `SnapshotLifecycle` moved from `Marten.Events.Projections` to
  `JasperFx.Events.Projections`.
- `OperationRole` moved from `Marten.Internal.Operations` to `Weasel.Core`.

### Foundation

- **AOT pillar foundation landed** (#2747 toward #2715 / #2746). New
  `Wolverine.AotSmoke` regression-guard project + `.github/workflows/aot.yml`
  workflow. Verifies the AOT-clean *subset* of Wolverine's surface (Envelope
  value-shape, DeliveryOptions, WolverineOptions configuration, scheduling
  helpers). The full per-file annotation pass and the eventual flip of
  `IsAotCompatible=true` on `Wolverine.csproj` is tracked in #2746.

## 5.37.2

### WolverineFx (core)

- Removed the experimental Wolverine-specific Roslyn source generator (`Wolverine.SourceGeneration`)
  and the `IWolverineTypeLoader` / `[WolverineTypeManifest]` / `CompositeWolverineTypeLoader`
  surface it produced. The compile-time handler-discovery path was never wired up to anything in
  steady state — handler graph compilation always falls back to `compileWithRuntimeScanning`, which
  has been the only code path exercised by tests and downstream consumers. Stripping it removes a
  netstandard2.0 analyzer DLL from the WolverineFx NuGet, the analyzer ProjectReference from
  `Wolverine.csproj`, the source-gen branches in `ExtensionLoader.ApplyExtensions`,
  `WolverineRuntime.HostService` startup, `HandlerGraph.Compile`, and `HandlerChain.AttachTypes`,
  plus the two `TypeLoaderManifestModule*` test fixtures and their aggregation tests. The
  `JasperFx.SourceGeneration` analyzer (separate package) is unaffected.

## 5.37.0

### WolverineFx.Marten

- Fixed durable local messages from a main Marten store being routed to the wrong inbox when handled by an
  ancillary-store handler (`[MartenStore(typeof(...))]`). The publisher-stamped `envelope.Store` (the main store)
  carried through the inbox and `FlushOutgoingMessagesOnCommit` then pointed at the publisher's
  `wolverine_incoming_envelopes` table while the receiving Marten session was connected to the ancillary
  database, surfacing as `42P01: relation "public.wolverine_incoming_envelopes" does not exist`. Closes #2669.
  - The receiving handler's ancillary-store association now wins over the publisher's: `assignAncillaryStoreIfNeeded`
    in `DurableLocalQueue` and `DurableReceiver` no longer short-circuits when the envelope already has a `Store`.
  - The Marten listener's in-transaction inbox `UPDATE` is gated on `Uri` equality (not `IMessageStore.Id`) so
    cross-store envelopes deterministically skip the in-transaction shortcut and the envelope's owning store
    handles the mark-handled separately.

### WolverineFx.RabbitMQ

- Added a public fluent API for multi-node RabbitMQ cluster failover via `RabbitMqTransportExpression.AddClusterNode(...)`.
  Two repeatable overloads — `AddClusterNode(string hostName, int port = -1)` (copies the factory's `Ssl` settings
  onto the new endpoint) and `AddClusterNode(AmqpTcpEndpoint endpoint)` (power-user). Cluster nodes propagate to
  virtual-host tenants and surface in connection diagnostics. Closes #2659.

### WolverineFx.Polecat

- Fixed `FlushOutgoingMessagesOnCommit` `NullReferenceException` on every Polecat-backed handler. The
  `OutboxedSessionFactory` was constructing the listener with `null!` for the `SqlServerMessageStore` on the
  assumption that a post-construction setter would fill it in — but the listener's field is `readonly`. Replaced
  with a `resolveSqlServerMessageStore()` helper that mirrors `PolecatEnvelopeTransaction`'s two-shape
  resolution (`SqlServerMessageStore` + `MultiTenantedMessageStore { Main: SqlServerMessageStore }`) so multi-tenanted
  Polecat works too. Closes #2668.

### WolverineFx (core)

- New `DocumentStores` collection on `ServiceCapabilities`, mirroring the existing `EventStores` walk for the
  document side. Walks `IDocumentStoreUsageSource` registrations (Marten and Polecat both implement it via
  `IDocumentStore`), dedupes by `Subject` URI to avoid double-counting when the same instance wears both
  event-store and document-store hats, and stuffs `DocumentStoreUsage` snapshots into the capabilities surface
  so CritterWatch can render document-side configuration the same way it already renders event stores.

### Dependencies

- Bumped `JasperFx` 1.28.2 → 1.29.0, `JasperFx.Events` 1.31.1 → 1.33.1, `Marten` + `Marten.AspNetCore`
  8.32.0 → 8.35.0. The bumped JasperFx packages provide the `IDocumentStoreUsageSource` and `DocumentStoreUsage`
  types the new capability surface depends on.

## 5.36.2

### WolverineFx (core)

- Reworked the EF Core + outbox flush pipeline to ensure cascading messages aren't sent before the EF Core
  transaction commits. New `IFlushesMessages` abstraction; `EnrollDbContextInTransaction` and the HTTP chain
  codegen now route the post-handler flush through it so the commit-then-flush ordering is enforced
  consistently. Companion fix to 5.36.1 — the codegen guard from 5.36.1 stopped emitting the duplicate flush
  call, this release reworks the underlying machinery so the ordering invariant holds even under future
  codegen changes.

## 5.36.1

### WolverineFx.EntityFrameworkCore

- Fixed a code-generation bug where the EF Core transactional middleware in Eager mode (the default) emitted
  a duplicate `messageContext.FlushOutgoingMessagesAsync()` call BEFORE the wrapping
  `efCoreEnvelopeTransaction.CommitAsync(...)`. The early flush sent cascading messages through the transport
  sender while the EF Core transaction (and its `wolverine_outgoing_envelopes` row) was still uncommitted, so
  the post-send `IMessageOutbox.DeleteOutgoingAsync` ran on a separate connection that couldn't see the
  uncommitted INSERT — the row was left stranded for the durability agent to re-send (at-least-once instead of
  exactly-once). Only manifested on HTTP endpoints; message handler chains were unaffected. Lightweight mode
  is unchanged. Reported via the sample at https://github.com/dmytro-pryvedeniuk/outbox.

## 5.36.0

### WolverineFx.Http

- Added native API versioning support via `Asp.Versioning.Abstractions` 10.x. Supports URL-segment versioning
  (`/v1/...`, `/v2/...`), sunset/deprecation policies with RFC 9745/8594/8288 response headers, and automatic
  OpenAPI document partitioning with Swashbuckle/Scalar/Microsoft.AspNetCore.OpenApi. No dependency on
  `Asp.Versioning.Http` — versioning is driven entirely via `IHttpPolicy`.
  See [versioning guide](docs/guide/http/versioning.md).
