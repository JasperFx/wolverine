# A Big Week for the Critter Stack

The post-GA cadence has not let up. Between **June 22 and June 29**, we shipped **three Wolverine releases, three Marten releases, and three Polecat releases** — a week heavy on database-backed messaging, brand-new interoperability with the rest of the .NET messaging ecosystem, and a steady drumbeat of work to make every part of the stack more observable and more manageable from CritterWatch.

Here's a tour of what landed.

---

## Release Timeline

| Day | Wolverine | Marten | Polecat |
|-----|-----------|--------|---------|
| Jun 22 | — | 9.10.0 | — |
| Jun 23 | 6.14.0 | — | 4.5.2 |
| Jun 25 | — | — | 4.6.0 |
| Jun 26 | 6.15.0 | 9.11.0 | — |
| Jun 29 | 6.16.0 | 9.12.0 | 4.7.0 |

---

<!--
  CRITTERWATCH SECTION — Jeremy to write by hand.

  This is where the CritterWatch story for the week goes: what's newly
  visible, what's newly manageable, and how the descriptor/observability
  work across Wolverine, Marten, and Polecat feeds the dashboard.
-->

## CritterWatch

_(Coming — written separately.)_

---

## Wolverine

Three releases this week, but the headline is clear: **database-backed messaging got faster, and Wolverine now speaks the queueing protocols of the two biggest .NET messaging frameworks.**

### 🚀 Database queue performance

Both the PostgreSQL and SQL Server transports got a focused performance pass that targets the hottest part of any database-backed queue: the dequeue path.

- **PostgreSQL transport** — indexed dequeue path plus more robust idempotency handling ([#3278](https://github.com/JasperFx/wolverine/pull/3278)).
- **SQL Server transport** — indexed dequeue path plus an **opt-in clustered queue layout** so the physical table organization matches how the queue is actually read ([#3277](https://github.com/JasperFx/wolverine/pull/3277)).

On SQL Server the new layout is one fluent call. Clustering the queue and scheduled tables on a monotonic `seq` identity (instead of the previous random-`Guid` clustered key) turns FIFO dequeue into a clustered seek with physically contiguous deletes:

```csharp
opts.UseSqlServerPersistenceAndTransport(connectionString)
    .OptimizeQueueThroughput();
```

The raw-DDL benchmark behind the PR tells the story — same hardware, same workload:

| Layout | Throughput | p50 latency | p99 latency |
|--------|-----------:|------------:|------------:|
| baseline (clustered `Guid`, no index) | 98/s | 845 ms | 1,860 ms |
| **`OptimizeQueueThroughput()`** (clustered `seq`) | **34,612/s** | **2.4 ms** | **3.7 ms** |

If you lean on Wolverine's database queues — whether as a no-broker option or to keep messaging transactionally consistent with your business data — the indexed dequeue path is a free win on upgrade. `OptimizeQueueThroughput()` is opt-in specifically because enabling it on an existing database triggers a one-time queue-table rebuild, so it's a maintenance-window change for existing systems and a no-brainer for new apps.

📖 [SQL Server transport docs](https://wolverinefx.io/guide/messaging/transports/sqlserver.html) · 📖 [PostgreSQL transport docs](https://wolverinefx.io/guide/messaging/transports/postgresql.html)

### 🆕 Interop with MassTransit and NServiceBus over SQL Server and PostgreSQL

This is the big one. Wolverine can now **send to and receive from MassTransit and NServiceBus applications using each framework's own SQL Server or PostgreSQL queueing** — reading and writing their native tables directly, no shared broker required.

Landed across 6.14.0 and 6.16.0:

- **NServiceBus over SQL Server** ([#3198](https://github.com/JasperFx/wolverine/pull/3198))
- **NServiceBus over PostgreSQL** ([#3201](https://github.com/JasperFx/wolverine/pull/3201))
- **MassTransit over PostgreSQL** ([#3203](https://github.com/JasperFx/wolverine/pull/3203))
- Each interop transport is pinned to a dedicated database under multi-tenanted storage ([#3271](https://github.com/JasperFx/wolverine/pull/3271)), `Seq` is indexed on the NServiceBus PostgreSQL queue table ([#3205](https://github.com/JasperFx/wolverine/pull/3205)), and a **shared `DatabaseListener` base** now backs the polling loop across all of these ([#3206](https://github.com/JasperFx/wolverine/pull/3206)).

For **NServiceBus**, Wolverine reads and writes the NServiceBus queue tables directly — one table per queue with a JSON `Headers` column and a raw `Body` column:

```csharp
using Wolverine.SqlServer.Transport.NServiceBus;

builder.UseWolverine(opts =>
{
    // Wolverine's own durable inbox/outbox still lives in SQL Server
    opts.PersistMessagesWithSqlServer(connectionString, "wolverine");

    opts.UseNServiceBusSqlServerInterop();

    // Publish to an NServiceBus endpoint's queue table
    opts.PublishMessage<OrderPlaced>().ToNServiceBusSqlServerQueue("nsb");

    // Listen to Wolverine's own queue table and use it for replies
    opts.ListenToNServiceBusSqlServerQueue("wolverine").UseForReplies();

    // Bind NServiceBus interface-typed messages to Wolverine's concrete types
    opts.Policies.RegisterInteropMessageAssembly(typeof(IOrderContract).Assembly);
});
```

PostgreSQL is identical with the `UseNServiceBusPostgresqlInterop()` / `ListenToNServiceBusPostgresqlQueue()` / `ToNServiceBusPostgresqlQueue()` trio. **MassTransit** is a different shape — its SQL transport is a function-driven, two-table model (`transport.message` + `transport.message_delivery`) that MassTransit owns and migrates, so Wolverine calls its stored functions rather than touching a table:

```csharp
using Wolverine.Postgresql.Transport.MassTransit;

builder.UseWolverine(opts =>
{
    opts.PersistMessagesWithPostgresql(connectionString, "wolverine");

    opts.UseMassTransitPostgresqlInterop(autoProvision: true);

    opts.PublishMessage<OrderPlaced>().ToMassTransitPostgresqlQueue("masstransit");
    opts.ListenToMassTransitPostgresqlQueue("wolverine").UseForReplies();

    opts.Policies.RegisterInteropMessageAssembly(typeof(IOrderContract).Assembly);
});
```

These join the existing Amazon SQS interop options (which also picked up two bug fixes this week, [#3190](https://github.com/JasperFx/wolverine/pull/3190)) and a fix to map Wolverine's `TenantId` from incoming MassTransit messages ([#3192](https://github.com/JasperFx/wolverine/pull/3192)). The practical upshot: you can introduce Wolverine into an existing MassTransit or NServiceBus shop **incrementally**, service by service, over infrastructure both sides already trust.

📖 [Interop with NServiceBus over database transports](https://wolverinefx.io/tutorials/interop.html#interop-with-nservicebus-over-database-transports) · 📖 [Interop with MassTransit over database transports](https://wolverinefx.io/tutorials/interop.html#interop-with-masstransit-over-database-transports)

### 🔭 Observability & health

A large share of the week's Wolverine work exists to make running systems legible — much of it surfaced directly through CritterWatch:

- A shared **`BackgroundReceiveLoop`** with receive-loop health reporting, now adopted across SQS, Redis, the PostgreSQL queue, the SQL Server queue, and Kafka ([#3236](https://github.com/JasperFx/wolverine/pull/3236)).
- **Transport connection state** surfaced in `EndpointHealthSnapshot`, with a new `IReportConnectionState` implemented for NATS, MQTT, Pulsar, and Redis ([#3231](https://github.com/JasperFx/wolverine/pull/3231)), plus a **force-restart path for stuck listeners** ([#3232](https://github.com/JasperFx/wolverine/pull/3232)).
- A **sanitized, credential-free broker connection summary** on `BrokerDescription` ([#3272](https://github.com/JasperFx/wolverine/pull/3272)) — so the dashboard can show you *where* a broker points without ever leaking secrets.
- Richer **metrics**: every instrument tagged with a `source` service name ([#3221](https://github.com/JasperFx/wolverine/pull/3221)), dimensional inbox/outbox/scheduled gauges, and configurable histogram buckets ([#3224](https://github.com/JasperFx/wolverine/pull/3224)).
- The discovered **gRPC endpoint manifest** is now exposed via a `ServiceCapabilities` descriptor source ([#3268](https://github.com/JasperFx/wolverine/pull/3268), [#3266](https://github.com/JasperFx/wolverine/pull/3266)), and RabbitMQ sending endpoints are now properly named in health snapshots ([#3273](https://github.com/JasperFx/wolverine/pull/3273)).

📖 [Instrumentation and Metrics](https://wolverinefx.io/guide/logging.html) · 📖 [Diagnostics](https://wolverinefx.io/guide/diagnostics.html)

### 🐛 Reliability fixes & Pulsar

6.14.0 also closed out a **major Pulsar re-evaluation effort** — DLQ/retry precedence, initial subscription position, multi-topic and regex subscriptions, native per-message redelivery, acknowledgment-strategy choice, a Reader interface for bounded replay and non-durable hot-tail, a tiered retry-letter error policy, producer deduplication, and both JSON and Avro schema support with broker-side registration ([#3194](https://github.com/JasperFx/wolverine/pull/3194)–[#3215](https://github.com/JasperFx/wolverine/pull/3215)).

Two of those are worth showing. Pulsar's defining feature is broker-side **schema** registration and compatibility checking — now a single fluent call, with the message body still owned by Wolverine's serialization:

```csharp
opts.PublishMessage<OrderPlaced>()
    .ToPulsarTopic("persistent://public/default/orders")
    .UseJsonSchema<OrderPlaced>();   // or UseAvroSchema<T>() for Avro on the wire
```

And the new **tiered retry-letter policy** — the Pulsar analogue of the Kafka transport's `MoveToKafkaRetryTopic` — expresses native redelivery delays as a first-class, discoverable error policy:

```csharp
// On failure: redeliver after 5s, then 30s, then 2m, then dead-letter.
opts.OnException<TransientException>()
    .MoveToPulsarRetryTopic(5.Seconds(), 30.Seconds(), 2.Minutes());
```

📖 [Pulsar schema support](https://wolverinefx.io/guide/messaging/transports/pulsar.html#schema-support) · 📖 [Tiered retry-letter policy](https://wolverinefx.io/guide/messaging/transports/pulsar.html#tiered-retry-letter-policy) · 📖 [Producer deduplication](https://wolverinefx.io/guide/messaging/transports/pulsar.html#producer-deduplication)

Plus targeted reliability fixes: a RabbitMQ agent that could latch `Disconnected` after a channel-only shutdown ([#3187](https://github.com/JasperFx/wolverine/pull/3187)), stable node identity for storeless Solo hosts ([#3189](https://github.com/JasperFx/wolverine/pull/3189)), and re-attaching the sender wire tap to recovered envelopes ([#3276](https://github.com/JasperFx/wolverine/pull/3276)).

---

## Polecat — Making It More Robust

Polecat shipped three releases this week (4.5.2, 4.6.0, 4.7.0), and the through-line is **hardening**: fewer sharp edges, more parity with Marten's behavior, and a real document-metadata story.

### 🛡️ Robustness & correctness fixes

- **Repopulate the natural-key lookup table on projection rebuild** ([#261](https://github.com/JasperFx/polecat/pull/261)) — rebuilds no longer leave natural-key lookups stale (mirrored by the same fix in Marten, below).
- **`Patch().Set()` now honors `EnumStorage`** ([#264](https://github.com/JasperFx/polecat/pull/264)) and **supports `DateTime`/`DateTimeOffset`/`DateOnly`/`TimeOnly`** ([#265](https://github.com/JasperFx/polecat/pull/265)).
- **Sequential GUIDs for auto-assigned document ids** ([#245](https://github.com/JasperFx/polecat/pull/245)) — far friendlier to index locality than random GUIDs.
- `AsString` enum LINQ predicates honor the `JsonNamingPolicy` ([#224](https://github.com/JasperFx/polecat/pull/224)), computed-column indexes are usable by the LINQ translator ([#225](https://github.com/JasperFx/polecat/pull/225)), on-the-fly event-store schema creation and `InitialData` seeding work on startup ([#233](https://github.com/JasperFx/polecat/pull/233)), and `IEventStore.Identity` now varies by `StoreName` so multiple stores stay distinct ([#208](https://github.com/JasperFx/polecat/pull/208)).

### 🆕 Document metadata

A genuinely new capability area: opt-in document metadata, end to end — mirroring Marten's metadata model so the two stores behave alike. Enable the columns you want with a fluent DSL (or attributes) ([#251](https://github.com/JasperFx/polecat/pull/251), [#252](https://github.com/JasperFx/polecat/pull/252)):

```csharp
opts.Schema.For<Order>().Metadata(m =>
{
    m.LastModifiedBy.Enabled = true;
    m.CorrelationId.Enabled = true;
    m.CreatedAt.MapTo(x => x.CreatedDate);   // project a column onto your own member
});
```

Then read just the metadata for a row — no document body deserialization — via the new `MetadataForAsync<T>` API ([#253](https://github.com/JasperFx/polecat/pull/253)):

```csharp
DocumentMetadata metadata = await session.MetadataForAsync(order);
// metadata.Version, .LastModified, .LastModifiedBy, .CorrelationId, .CausationId, ...
```

Rounding it out: an opt-in `user_name` (`LastModifiedBy`) event-metadata column ([#248](https://github.com/JasperFx/polecat/pull/248)), **auto-seeding of `CorrelationId`/`CausationId` from `Activity.Current`** on session open ([#250](https://github.com/JasperFx/polecat/pull/250)), and session-level `Headers` with `SetHeader`/`GetHeader` ([#249](https://github.com/JasperFx/polecat/pull/249)).

### 🔭 Observability & CritterWatch

- An opt-in `polecat.event.append` **OpenTelemetry counter** ([#247](https://github.com/JasperFx/polecat/pull/247)) and runtime event-append observations via `IEventStoreInstrumentation.AppendObserver` ([#215](https://github.com/JasperFx/polecat/pull/215)).
- **`IDocumentStoreDiagnostics`** with an enriched mapping descriptor ([#210](https://github.com/JasperFx/polecat/pull/210)), structured partitioning in the `DocumentMappingDescriptor` ([#214](https://github.com/JasperFx/polecat/pull/214)), and **metadata capabilities + an `IEventStore` bridge with tenant-scoped document diagnostics** ([#254](https://github.com/JasperFx/polecat/pull/254)) — the same descriptor surface Marten exposes, so CritterWatch sees Polecat stores the same way it sees Marten.

### 🆕 Range partitioning

Declarative range partitioning for document tables ([#257](https://github.com/JasperFx/polecat/pull/257), [#212](https://github.com/JasperFx/polecat/pull/212)), now with a Marten-parity fluent surface — the classic time-series retention pattern:

```csharp
// Marten manages the boundaries:
opts.Schema.For<MetricsSample>().PartitionOn(x => x.BucketEnd).ByRange(jan, feb, mar);

// Or let a DBA / pg_partman own SPLIT/SWITCH/DROP at runtime for retention:
opts.Schema.For<MetricsSample>().PartitionOn(x => x.BucketEnd).ByExternallyManagedRange(jan, feb);
```

`ByExternallyManagedRange(...)` provisions the partitions once and then never reconciles them, so a later schema apply won't clobber the months your retention job has been splitting and dropping.

📖 [Wolverine + Polecat integration guide](https://wolverinefx.io/guide/durability/polecat/) · 📦 [Polecat on GitHub](https://github.com/JasperFx/polecat)

---

## Marten

Three releases (9.10.0, 9.11.0, 9.12.0), with a mix of concurrency-hardening, new partitioning options, and — again — observability work feeding CritterWatch.

### 🐛 Concurrency & correctness

- **Close the `mt_events_sequence` gap on concurrent Quick OCC failures** ([#4771](https://github.com/JasperFx/marten/pull/4771)) — a first contribution from [@KMDjkb](https://github.com/KMDjkb). Under truly concurrent `FetchForWriting` + Quick-append writes to the same stream, a losing transaction could burn a sequence value it never rolled back, leaving a permanent gap that stalls the async daemon's high-water mark. A new opt-in option takes a `FOR UPDATE` lock in the OCC path so the loser blocks and raises a clean concurrency error *before* consuming a sequence value — no schema migration required:

  ```csharp
  opts.Events.UseExclusiveLockOnConcurrentAppends = true;
  ```
- **Fix a false `ConcurrencyException`** from non-`RETURNING` event ops in a batched `SaveChanges` ([#4784](https://github.com/JasperFx/marten/pull/4784)).
- **Repopulate `mt_natural_key` on projection rebuild** ([#4793](https://github.com/JasperFx/marten/pull/4793)) — the Marten side of the same natural-key fix that landed in Polecat.

### 🆕 Partitioning & queries

- **Range-partition a document table by a non-tenant date column** ([#4780](https://github.com/JasperFx/marten/pull/4780)) — the `PartitionOn(member, cfg)` API already existed; a Weasel 9.3.0 fix makes the date-keyed retention path stable across deployments and time zones (partition bounds are now compared by normalized instant rather than raw SQL literal, so migrations no longer report a spurious destructive rebuild).
- **Metadata-filtered document and event queries** ([#4792](https://github.com/JasperFx/marten/pull/4792)) — the diagnostics surface can now filter documents and events by `correlation_id` / `causation_id` / `last_modified_by`, honored only when the store actually captures that metadata column.

📖 [Document storage & date range partitioning](https://martendb.io/documents/storage.html) · 📖 [Document & event metadata](https://martendb.io/documents/metadata.html)

### 🔭 Observability & CritterWatch

- **`IDocumentStoreDiagnostics`** with an enriched mapping descriptor ([#4776](https://github.com/JasperFx/marten/pull/4776)) and populated event/document **metadata capabilities** with tenant-scoped document diagnostics ([#4790](https://github.com/JasperFx/marten/pull/4790)).
- **Runtime event-append observations via `IEventStoreInstrumentation`** ([#4783](https://github.com/JasperFx/marten/pull/4783)) and an exact-identity `DeleteProjectionProgressByShardNameAsync` for surgical projection-progress management ([#4786](https://github.com/JasperFx/marten/pull/4786)).

---

## The Common Thread

Three themes ran through all nine releases this week:

1. **Database-backed messaging matured** — Wolverine's PostgreSQL and SQL Server queues got faster, and now interoperate directly with MassTransit and NServiceBus over the same databases.
2. **Polecat got tougher** — a stack of correctness fixes, sequential GUIDs, a full document-metadata story, and range partitioning.
3. **Everything got more observable** — diagnostics descriptors, instrumentation hooks, OpenTelemetry counters, connection-state reporting, and credential-safe broker summaries across Wolverine, Marten, and Polecat — all converging on a single, consistent surface for CritterWatch to manage.

As always: upgrade, and [tell us what breaks](https://github.com/JasperFx/wolverine/issues). This week's patch cadence is the proof that we listen.
