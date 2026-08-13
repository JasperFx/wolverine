# Changelog

## Unreleased

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
