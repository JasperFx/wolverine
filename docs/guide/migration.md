# Migration Guide

## Key Changes in 7.0

::: warning
7.0 is in development. Items below describe the changes made so far, and the released 7.0 may add to or refine this
list.
:::

### At a glance: settings & APIs that changed in 7.0

| Setting / API | 6.x | 7.0 | Migration action |
|---|---|---|---|
| `WolverineFx.Kafka` client library | Confluent.Kafka (librdkafka) | **Dekaf** (pure C#, KIP-848-only) *(BREAKING for custom config, mappers, schema-registry serializers; requires Kafka 4.0+)* | Swap Confluent config types for [Wolverine's Kafka option classes](#kafka-transport-now-runs-on-dekaf-breaking); wire format is unchanged |
| `UseCooperativeStickyAssignment()`, `UseNextGenerationRebalanceProtocol()` | rebalance opt-ins | **removed** *(BREAKING)* | Delete the calls; KIP-848 rebalances are always incremental |
| `KafkaProducerOptions.Acks` below `All` | accepted (librdkafka producers were not idempotent by default) | **startup fails** unless `EnableIdempotence = false` *(BREAKING)* | Set `EnableIdempotence = false` alongside the acks, or use `Acks.All`; see [Kafka behaviour changes](#kafka-behaviour-changes) |
| `CommitMode.PerMessage` | committed before the handler's completion returned | committed on the listener's next consume pass, at most about a second later, one commit per pass | None |
| `OffsetCommitMode.Auto` offset storage | stored when the record is delivered | stored once the listener asks for the next record (`AfterProcessing`) | None; inline listeners and one-at-a-time durable listeners now redeliver after a crash instead of losing the record |
| Kafka client option names | Confluent's (`SecurityProtocol`, `EnableAutoCommit`, `*Ms` integers) | Dekaf's (`UseTls`, `OffsetCommitMode`, `TimeSpan`s) *(BREAKING)* | Rename the properties; see [the mapping](#kafka-transport-now-runs-on-dekaf-breaking) |
| Kafka producers | one per sending topic, plus dead letter and retry producers, again per tenant | one per cluster for each distinct set of producer options | None; give a topic its own options with `ConfigureProducer(...)` to keep it on its own producer |
| `InlineKafkaSender`, `KafkaSenderProtocol` | `IAsyncDisposable` | not disposable *(BREAKING)* | Drop any `DisposeAsync()` calls; the transport disposes the producers |
| Async serializers on the send path | `IMessageSerializer.Write` called synchronously | `IAsyncMessageSerializer.WriteAsync` awaited, at publish time for durable messages | None; see [async serializers](#async-serializers-are-awaited-when-sending) |
| `BackgroundReceiveLoop.UsesBlockingIteration` | opt-in dedicated thread for blocking iterations | **removed** *(BREAKING)* | Make the iteration async; see [transport author changes](#disposal-and-receive-loop-changes-for-transport-authors-breaking) |
| `BatchedSender`, `InlineSendingAgent`, `ListeningAgent` | `IDisposable` | `IAsyncDisposable` only *(BREAKING)* | Call `await DisposeAsync()` |
| `ISenderRequiresCallback` | extends `IDisposable` | does not *(BREAKING)* | Implement disposal on the sender itself if it needs it |
| `UseAutomaticForwarderDiscovery()`, `WolverineOptions.EnableMessageCausationTracking`, `Policies.PublishAgentEvents`, `AgentAssignmentsChanged`, `ApplyDeduplication(IChain)`, Fisher `TransportSchemaName`, `MqttEnvelope.ShouldRequeue` | `[Obsolete]` or unused | **removed** *(BREAKING)* | See [dead and obsolete flags removed](#dead-and-obsolete-flags-removed-breaking) |

### Kafka transport now runs on Dekaf (BREAKING)

`WolverineFx.Kafka` replaced the Confluent.Kafka client library with [Dekaf](https://thomhurst.github.io/Dekaf/), a pure C# Kafka client with no native librdkafka dependency that is friendly to Native AOT. The transport's fluent configuration API, commit modes, dead letter queue, retry topics, replay, multi-tenancy, and multi-topic listening all keep their shape (see [Kafka behaviour changes](#kafka-behaviour-changes) for what runs differently), but there are several visible changes:

- **Kafka 4.0+ broker required.** Dekaf speaks only the [KIP-848](https://cwiki.apache.org/confluence/display/KAFKA/KIP-848%3A+The+Next+Generation+of+the+Consumer+Rebalance+Protocol) consumer-group protocol, so consumers cannot join their groups on older brokers.
- **Wolverine-owned option classes replace the Confluent config objects.** `ConfigureConsumers()`, `ConfigureProducers()`, `ConfigureClient()`, and `AutoProvision(...)` keep their shapes, but now hand you `KafkaConsumerOptions`, `KafkaProducerOptions`, `KafkaClientOptions`, and `KafkaAdminClientOptions` from the `Wolverine.Kafka` namespace instead of Confluent's `ConsumerConfig` / `ProducerConfig` / `ClientConfig` / `AdminClientConfig`. The curated properties cover connectivity, SASL/SSL, group and fetch settings, and producer delivery settings; anything beyond them is reachable via `ConfigureConsumerBuilders()` / `ConfigureProducerBuilders()` / `ConfigureAdminClientBuilders()`, which expose Dekaf's fluent builders. Their names and types follow Dekaf's rather than Confluent's:
  - `SecurityProtocol = SecurityProtocol.Ssl` becomes `UseTls = true`; `SaslSsl` is `UseTls = true` plus a `SaslMechanism`, and `SaslPlaintext` is a `SaslMechanism` alone.
  - `SaslMechanism` is Dekaf's `Dekaf.Security.Sasl.SaslMechanism`. `Plain`, `ScramSha256` and `ScramSha512` are configured from these options; `Gssapi`, `OAuthBearer` and `AwsMskIam` stop the host from starting, and are configured on the builders with `WithGssapi(...)`, `WithOAuthBearer(...)` or `WithAwsMskIam(...)`.
  - `EnableAutoCommit = true` / `false` becomes `OffsetCommitMode = OffsetCommitMode.Auto` / `OffsetCommitMode.Manual`.
  - Millisecond integers become `TimeSpan`s named after Dekaf's builder methods: `AutoCommitIntervalMs` is `AutoCommitInterval`, `SessionTimeoutMs` is `SessionTimeout`, `HeartbeatIntervalMs` is `HeartbeatInterval`, and `LingerMs` is `Linger`.
  - `AutoOffsetReset`, `IsolationLevel` and `Acks` are Dekaf's enums. `AutoOffsetReset.ByDuration` takes its window from `AutoOffsetResetDuration`.

  `TopicSpecification` becomes Wolverine's `KafkaTopicSpecification` (with `ToNewTopic()` for hand-off to Dekaf's admin API), and `TopicCreation(...)` callbacks receive `Dekaf.Admin.IAdminClient`.
- **Custom envelope mappers change signature.** `IKafkaEnvelopeMapper` is now `IEnvelopeMapper<Dekaf.Consumer.ConsumeResult<string, byte[]>, OutgoingKafkaRecord>` — incoming records are Dekaf's `ConsumeResult`, outgoing records are Wolverine's mutable `OutgoingKafkaRecord` (`Key`, `Value` where null means a tombstone, `Headers`, `Timestamp`, `Partition`).
- **Schema registry serializer constructors changed.** `SchemaRegistryAvroSerializer` and `SchemaRegistryJsonSerializer` now build on the Dekaf.SchemaRegistry packages: constructors take a `Dekaf.SchemaRegistry.ISchemaRegistryClient` (`new SchemaRegistryClient(new SchemaRegistryConfig { Url = "..." })`) plus optional `AvroSerializerConfig` / `JsonSchemaSerializerConfig` (and optional `JsonSerializerOptions` for JSON) instead of the Confluent.SchemaRegistry.Serdes types. The wire format is still the Confluent framing, so records interoperate with Confluent serdes on the other side.
- **Rebalance opt-ins removed.** `UseCooperativeStickyAssignment()` and `UseNextGenerationRebalanceProtocol()` are gone from both the transport and listener configuration. Under KIP-848 rebalances are always broker-driven and incremental, so there is nothing to opt into. Delete the calls. `UseStaticMembership(...)` is unchanged and fully supported.
- **Full wire interop.** The transport remains wire-compatible with applications using Confluent.Kafka or any other Kafka client: keys, values, headers, timestamps, and tombstones all round-trip.

See the [Kafka transport docs](/guide/messaging/transports/kafka) for the full configuration surface.

### Kafka behaviour changes

- **Producers are idempotent by default, and acks are checked at startup.** Dekaf producers are idempotent unless `EnableIdempotence = false`, and an idempotent producer always waits for every in-sync replica. A producer configured with `Acks.None` or `Acks.Leader` and idempotence left on now stops the host from starting with an explanation, rather than failing every send (`Acks.None`) or being silently raised to `Acks.All` (`Acks.Leader`).
- **Offsets are committed by the listener's consume loop.** Handlers queue each finished offset, and the loop commits or stores them before it next polls, at least once a second. `CommitMode.PerMessage` therefore commits shortly after a message finishes rather than before its completion returns, and a pass commits every partition that advanced in one call. `BatchInterval` now commits as soon as its interval passes, instead of waiting for the next message to finish. When partitions are revoked, the offsets finished on them are committed before they are handed to another consumer.
- **`OffsetCommitMode.Auto` stores offsets after processing.** The offset of a record becomes committable once the listener asks for the next record, instead of when the record is delivered. Inline listeners, and durable listeners receiving one record at a time, now redeliver their in-flight record after a crash instead of losing it. Buffered listeners and durable micro-batches ask for the next record before processing finishes, so they behave as before.
- **A retry-tier record that is not due yet only holds back its own partition.** The listener pauses that partition and rewinds it to the record until the record's delay has passed, rather than waiting inside the consume loop, so the other partitions the consumer owns keep flowing.
- **Producers are shared per cluster.** Every topic without its own producer options, the dead letter topic and the retry-tier topics share one producer per cluster, and each broker-per-tenant cluster gets its own. Topics that share a producer share its send buffer, so a topic that fills it can slow sends to the others; give a topic its own options with `ConfigureProducer(...)` to keep it separate. Producers are flushed and disposed with the transport, not with each sender.
- **Health checks read the live Kafka clients.** The transport health check reports from the connection status of the running consumers and producers: Unhealthy when none has a live broker connection, Degraded when some have none or a producer's send buffer is full. It only falls back to an admin-client probe before any client has connected. Endpoint health reports consumer lag as the broker queue depth.

### Async serializers are awaited when sending

Wolverine now awaits `IAsyncMessageSerializer.WriteAsync` (the schema registry serializers, encryption, claim checks) when it batches outgoing messages, rather than calling the synchronous `Write`. For a durable message the serialization runs when the message is published, before it is written to the outbox, so the outbox write does not block on an async serializer. A serializer failure, such as an unreachable schema registry, now fails the handler that published the message and goes through its error policies, rather than surfacing later when the outbox is flushed.

### Disposal and receive loop changes for transport authors (BREAKING)

These only affect code that builds custom transports or drives Wolverine's sending and listening agents directly:

- `BatchedSender`, `InlineSendingAgent` and `ListeningAgent` are `IAsyncDisposable` only; replace `Dispose()` calls with `await DisposeAsync()`. Wolverine now also disposes senders and sender protocols that are only `IAsyncDisposable`.
- `ISenderRequiresCallback` no longer extends `IDisposable`.
- `BackgroundReceiveLoop.UsesBlockingIteration` is removed. Kafka was its only user, and its consumer is now async. A transport whose client only offers a blocking receive should run that receive on its own thread rather than inside the loop's iteration.

---

### Defaults that changed in 7.0: `RestoreV6Defaults()`

To take Wolverine 7 without its new runtime defaults, call `opts.RestoreV6Defaults()` once at the top of your
`UseWolverine` lambda:

```csharp
builder.Host.UseWolverine(opts =>
{
    opts.RestoreV6Defaults();
    // … the rest of your configuration
});
```

That puts back the Wolverine 6.x value of every default in the table below, including the ones that belong to
Wolverine.Http, the transports and the message stores, wherever in your configuration those are set up. Anything you
set explicitly still wins. Treat it as a temporary step: remove the call, then adopt each new default when you are
ready for it.

`RestoreV5Defaults()` is gone in 7.0. The one 6.0 default it covered, `ServiceLocationPolicy.NotAllowed`, is the 6.x
value, so `RestoreV6Defaults()` leaves it alone. If you still call `RestoreV5Defaults()`, replace it with
`opts.ServiceLocationPolicy = ServiceLocationPolicy.AllowedButWarn;`.

<!-- Every 7.0 default flip adds a row here. `Restore test` names the test class proving that RestoreV6Defaults()
     puts the 6.x value back; CoreTests.restore_v6_defaults_tests checks every row against it. -->

| Setting | 6.x | 7.0 | Opt-out | Restore test |
|---|---|---|---|---|
| `WolverineHttpOptions.RejectUnparseableQueryValues` | `false`: an unparseable query string value binds the parameter's default | `true`: it answers 400 with ProblemDetails | `opts.RejectUnparseableQueryValues = false` in `MapWolverineEndpoints()` | `restore_v6_http_defaults` |
| `WolverineHttpOptions.MapConcurrencyFailuresToConflict()` | off: a concurrency failure escapes as a 500 | on for the transactional endpoints: 409 with ProblemDetails, advertised in OpenAPI (under Marten 9.x, the exclusive-lock failure only with `MapMartenConcurrencyFailuresToConflict()`) | `opts.DoNotMapConcurrencyFailuresToConflict()` in `MapWolverineEndpoints()` | `restore_v6_http_defaults` |
| `WolverineHttpOptions.MapUnknownTenantToNotFound()` | off: `UnknownTenantIdException` escapes as a 500 | on for the tenanted endpoints: 404 with ProblemDetails, advertised in OpenAPI | `opts.DoNotMapUnknownTenantToNotFound()` in `MapWolverineEndpoints()` | `restore_v6_http_defaults` |
| `WolverineHttpOptions.ApplyCorrelationIdHeaderToAllEndpoints` | `false` | `true`: the `X-Correlation-ID` header reaches every endpoint, and the events it appends | `opts.ApplyCorrelationIdHeaderToAllEndpoints = false` in `MapWolverineEndpoints()` | `restore_v6_http_defaults` |
| `PushReceiverOptions.DeduplicateWithInbox` | `false` | `true` when the application has a message store | `o.DeduplicateWithInbox = false` in `MapWolverinePushReceiver()` | `push_receiver_5016` |
| `BrokerTransport.TenantedIdBehavior` (RabbitMQ, Azure Service Bus, SQS, SNS, Kafka, GCP Pub/Sub, Redis, NATS; also MQTT, MQTT 5 and Pulsar) | `FallbackToDefault`: a message for an unregistered tenant id goes to the default broker | `DeadLetterUnknownTenants`: it goes to dead letter storage and is sent nowhere | `.TenantIdBehavior(TenantedIdBehavior.FallbackToDefault)` (`ConfigureMultiTenancy(...)` on Redis and NATS) | `restore_v6_broker_tenancy_defaults` |
| `NatsTransportConfiguration.TrustTenantIdHeader` | `true`: a tenant-aware listener takes the tenant from the `tenant-id` header | not trusted: the tenant comes from the connection or subject prefix the message arrived on | `nats.TrustTenantIdHeader = true` | `NatsTenantIdHeaderTests` |
| `DurabilitySettings.MessageDeduplicationMode` | `None`: no deduplication table | `CompareByHash`: every message store provisions `wolverine_deduplication_hashed` and its cleanup job | `opts.Durability.MessageDeduplicationMode = MessageDeduplicationMode.None` | `restore_v6_defaults_tests` |
| `DurabilitySettings.EnableDeduplicatedResponses` | `false` | `true`: every message store provisions `wolverine_deduplicated_responses` and its cleanup job | `opts.Durability.EnableDeduplicatedResponses = false` | `restore_v6_defaults_tests` |
| `DurabilitySettings.AlwaysProvisionOptionalSchemaObjects` | not there (new in 7.0; `EnableOutgoingTimestampIndex` is removed): the inbox/outbox `timestamp` columns only exist with `InboxStaleTime`/`OutboxStaleTime`, `wolverine_nodes.load_factor` only with `CapacityAwareAssignment`, and the outbox stale index only with `EnableOutgoingTimestampIndex` | `true`: the relational stores always create all four, a one-time additive migration on first start; reading and writing them stays opt-in | `opts.Durability.AlwaysProvisionOptionalSchemaObjects = false` | `restore_v6_defaults_tests` |
| `WolverineOptions.EnvelopeIdGeneration` | `NewId` (MassTransit NewId: machine identity, process id and a timestamp) | `GuidV7` (`Guid.CreateVersion7()`). Either way, a SQL Server-only (or Polecat-only) application still switches to `NewIdSqlServerOrder` at startup | `opts.EnvelopeIdGeneration = EnvelopeIdGeneration.NewId` | `restore_v6_defaults_tests` |
| `DurabilitySettings.CompressAgentCommands` | `false`: agent commands travel under the plain intrinsic content type | `true`: agent commands are gzipped under `binary/wolverine+gzip`. **Every node in the cluster must run 6.44 or later before the first 7.0 node starts**; a node older than 6.44 rejects the command and stops taking agent assignments | `opts.Durability.CompressAgentCommands = false` | `restore_v6_defaults_tests` |
| `BatchingOptions.GroupByGroupId()` for a batched message type in a partitioned topology | off unless the batch executes on the topology's local slots: a batch envelope has no group id and draws a random slot downstream | on whenever the element type is in a partitioned topology (`GlobalPartitioned`, `PublishToPartitionedLocalMessaging` or a sharded listener topology), including with `ExecuteOnDedicatedLocalQueue()` or a named queue. Non-partitioned batching and a custom `IMessageBatcher` are unchanged | `batching.DoNotGroupByGroupId()` in `BatchMessagesOf<T>()` | `core_default_flips_5200` |
| `WithInMemoryIdempotency()` on `ProcessInParallelWithNativeAcks()` endpoints | off: no guard unless the listener asks for one | on, with the default 5 minute window and 100,000 id ceiling. Every other endpoint mode still opts in | `.WithoutInMemoryIdempotency()` on the listener | `core_default_flips_5200` |
| `DurabilitySettings.MessageIdentity` | `IdOnly`: an inbox row is identified by its envelope id | `IdAndDestination`: by its envelope id and the endpoint that received it. **This changes the primary key of the inbox table (and the dead letter table) in the database**; see [below](#messageidentity-defaults-to-idanddestination-in-7-0-schema-change) | `opts.Durability.MessageIdentity = MessageIdentity.IdOnly` | `restore_v6_defaults_tests` |

### Wolverine.Http defaults that changed in 7.0

Five Wolverine.Http defaults flipped in 7.0 (GH-5195). They are the rows of the table above that name
`WolverineHttpOptions` or `PushReceiverOptions`, and `RestoreV6Defaults()` puts back all five:

- **Strict query string binding.** A query string value that is present but cannot be parsed, such as `?page=abc` on an
  `int page` parameter, now answers 400 with a ProblemDetails naming the parameter, as MVC and minimal APIs do. In 6.x it
  bound the parameter's default value and the request went ahead. A missing value still binds the default in both
  modes. See [query string binding](/guide/http/querystring). F# code generation does not support strict binding yet,
  so an application that pre-generates F# code for an endpoint with a parsed query string parameter has to set
  `RejectUnparseableQueryValues = false`.
- **Concurrency failures answer 409.** The commit-time concurrency failures (`JasperFx.ConcurrencyException` and
  everything deriving from it, plus `JasperFx.Events.StreamLockedException`) on a transactional endpoint now answer 409
  with ProblemDetails instead of escaping as a 500, and the OpenAPI document advertises the 409. If you call
  `MapConcurrencyFailuresToConflict()`, `MapMartenConcurrencyFailuresToConflict()` or
  `MapPolecatConcurrencyFailuresToConflict()` yourself, your call replaces the default, so a custom filter or
  ProblemDetails factory still applies. With Marten 9.x, Marten's own `StreamLockedException` (from
  `FetchForExclusiveWriting`) does not derive from the JasperFx type yet, so keep calling
  `MapMartenConcurrencyFailuresToConflict()` if you want that one mapped to a 409 too. Marten 10 rebases it onto the
  JasperFx type, and from then on the default covers it. See
  [exception handling](/guide/http/exception-handling).
- **An unknown tenant answers 404.** An `UnknownTenantIdException` on an endpoint marked `[RequiresTenant]` or
  `[MaybeTenanted]` now answers 404 with ProblemDetails instead of escaping as a 500. A *missing* tenant id still
  answers 400.
- **The correlation id reaches every endpoint.** The request's `X-Correlation-ID` header is now applied wherever an
  endpoint's message context is created, so the events an endpoint appends record the caller's correlation id, not
  just the messages it sends. See [middleware](/guide/http/middleware).
- **Push receivers deduplicate with the inbox.** When the application has a message store, a push receiver now claims
  each message id in the durable inbox, so a redelivery is recognized instead of run again. See
  [serverless](/guide/serverless).

Two related behaviors need no action. `DefaultClaimInTransaction` stays opt-in. And since 6.51.2, a query string, header
or form value that is omitted binds the parameter's declared default (`int limit = 50` binds 50, not 0); that change
shipped without a flag, so there is nothing for `RestoreV6Defaults()` to restore (GH-5099).

### Broker-per-tenant tenancy is strict in 7.0

A broker is a trust boundary in 7.0 (GH-5196). Two defaults flipped, and `RestoreV6Defaults()` puts back both:

- **An unknown tenant id is dead lettered, not sent to the default broker.** With tenants registered on a transport
  (`AddTenant()`), a message with no tenant id still goes to the default broker and a message for a registered tenant
  goes to that tenant's broker only. A message for a tenant id that no registered tenant matches is moved to dead
  letter storage with an `UnknownBrokerTenantException` naming the tenant id, the message type and the endpoint, and
  is sent nowhere: not to the default broker, not to another tenant's. The exception derives from
  `UnknownTenantIdException`. The dead letter goes to the **main** message store (there is no tenant database for a
  tenant nobody registered) and keeps the envelope's tenant id. With the durable outbox, a message the outbox already
  holds is removed from it once its dead letter is written, so the durability agent never sends it. With **no message
  store** there is nowhere to dead letter it, so the send throws `UnknownBrokerTenantException` instead. Tenant ids
  match exactly (ordinal, case sensitive, no trimming). Explicit `FallbackToDefault`, `TenantIdRequired` and
  `IgnoreUnknownTenants` behave as they did in 6.x. `ConfigureMultiTenancy()` on Redis and NATS no longer pins
  `FallbackToDefault` when called without an argument.
- **NATS does not trust the `tenant-id` header.** A tenant-aware NATS listener takes the tenant from the tenant's own
  connection, or from the subject's tenant prefix, so a producer with publish rights on tenant A's subjects can no
  longer stamp a message as tenant B. `NatsTransportConfiguration.TrustTenantIdHeader` is now a `bool?`; null means
  the default. The unused `NatsTransportConfiguration.TenantIdResolver` property and `ITenantIdResolver` interface
  were removed.

During a rolling upgrade, 6.x nodes in the same cluster still send unknown tenant ids to the default broker until they
are upgraded.

### Deduplication storage is on by default; `EnableMessageDeduplication` removed (BREAKING)

`Durability.MessageDeduplicationMode` now defaults to `CompareByHash` (it was `None`), and
`Durability.EnableDeduplicatedResponses` now defaults to `true` (it was `false`). So every message store, in every
role and tenant database, provisions the `wolverine_deduplication_hashed` and `wolverine_deduplicated_responses` tables
on the first deploy of 7.0, each with a cleanup job that runs every five minutes. Storage alone deduplicates nothing:
handlers and endpoints still opt in with `[Deduplicated]`, `[DeduplicatedWithResponse]` or
`Policies.RequireDeduplicationId()`. A `[Deduplicated]` handler now works without any extra configuration.

To keep the 6.x schema, turn either one off or call `opts.RestoreV6Defaults()`, which turns both off:

```csharp
opts.Durability.MessageDeduplicationMode = MessageDeduplicationMode.None;
opts.Durability.EnableDeduplicatedResponses = false;
```

If you provision the schema yourself (`AutoBuildMessageStorageOnStartup = AutoCreate.None`), Wolverine will not create
these tables for you. Before you deploy 7.0, run `resources setup` or `db-apply` (or call `IHost.SetupResources()`)
against every message database, including each tenant database. Otherwise, opt out as shown above. If you do
neither, the startup check still passes, because it only looks for the envelope and node tables. But the cleanup jobs,
and any handler or endpoint that uses deduplication, will then fail against the missing tables.

An explicit setting wins over both the 7.0 default and `RestoreV6Defaults()`, in either order. Registering a recurring
message still turns deduplication on, even after `RestoreV6Defaults()`, because the recurring agent relies on it.

The obsolete `Durability.EnableMessageDeduplication` flag is gone. Replace `EnableMessageDeduplication = true` with
`MessageDeduplicationMode = MessageDeduplicationMode.CompareByHash` (or just delete the line), and `false` with
`MessageDeduplicationMode = MessageDeduplicationMode.None`.

Why defaults rather than creating the tables only when something uses them (GH-5194): we checked first whether
Wolverine knows which handlers and endpoints use deduplication before it builds the storage, and it does not. A message
store decides which tables it owns when it is constructed and initialized, before handler discovery runs at startup.
The `resources setup` and `db-apply` commands build the schema without discovering handlers at all. And an HTTP
endpoint mapped from a `Startup` class can be discovered after the storage has already migrated. Creating the tables
on use would have left a pre-provisioned database without the table a `[Deduplicated]` handler needs.

### Optional message storage schema objects are always created in 7.0

Wolverine 7 always creates a handful of small, additive schema objects in the relational message stores
(PostgreSQL, SQL Server, MySQL, Oracle and SQLite), whether or not you use the feature that reads them:

* the `timestamp` column on the incoming envelope table (used by `DurabilitySettings.InboxStaleTime`)
* the `timestamp` column on the outgoing envelope table (used by `DurabilitySettings.OutboxStaleTime`)
* the index on the outgoing `timestamp` column for the `ServerlessWithOutbox` durability pass
* the nullable `load_factor` column on `wolverine_nodes` (used by `DurabilitySettings.CapacityAwareAssignment`)

Only the schema changed. Nothing reads or writes these objects unless its own setting is on, so the behavior stays
opt-in. What you get is that turning on one of those settings later is a configuration change, not a schema migration.

`DurabilitySettings.EnableOutgoingTimestampIndex` is gone, because the index is always there now. Remove the line.

What to watch for on the upgrade:

* The first Wolverine 7 node migrates the message storage. If you run with
  `AutoBuildMessageStorageOnStartup = AutoCreate.None`, apply the migration with `dotnet run -- resources setup` (or
  your own migration process) as part of the deployment, and expect a schema check to report these objects as missing
  until you do.
* Wolverine stopped dropping unknown columns from its envelope and queue tables in 6.50. Every application sharing the
  same message storage schema has to be on 6.50 or later before the first 7.0 node starts, or an older node could drop
  the new columns again.
* On PostgreSQL the outgoing `timestamp` column defaults to `clock_timestamp()`, which is volatile, so adding it to an
  existing table rewrites that table under an exclusive lock. That's quick for a typical outbox, but if yours has a
  large backlog, migrate while it is drained.

To keep the 6.x layout, call `opts.RestoreV6Defaults()` or set
`opts.Durability.AlwaysProvisionOptionalSchemaObjects = false`. Each column is then only created when its setting is
on, and the outgoing timestamp index isn't created.

### `GuidV7` is the default envelope id in 7.0

Wolverine 7 creates envelope ids with `Guid.CreateVersion7()` (`EnvelopeIdGeneration.GuidV7`) instead of MassTransit's
NewId (GH-5197). NewId builds its ids from the machine's network identity, the process id and a timestamp, so
containers, Cloud Run instances and serverless functions that share a network identity can draw the same ids. `GuidV7`
ids carry a millisecond timestamp and RFC 9562 random bits, so they don't depend on anything about the machine. They
still sort by time in a PostgreSQL `uuid` column, a MySQL `CHAR(36)` column and a SQLite `TEXT` column, and on Oracle's
`RAW(16)` they are no worse than NewId was.

SQL Server is the exception, and nothing changes there. When every message store in the application is SQL Server
(including the SQL Server transport, EF Core on SQL Server and Polecat) and you haven't set `EnvelopeIdGeneration`
yourself, Wolverine still switches to `EnvelopeIdGeneration.NewIdSqlServerOrder` at startup, which keeps SQL Server's
clustered envelope tables in order. See [envelope IDs for SQL Server](/guide/messages#envelope-ids-for-sql-server).

What to watch for on the upgrade:

* There is no schema change. Envelope ids are still `Guid`s, and old and new ids live side by side in the same tables.
* Ids created in the same millisecond are in random order, where NewId ids from one process were strictly sequential.
  Nothing in Wolverine relies on that order, but check your own code if you sort envelopes by id.
* `optimizeEnvelopeIdsForSqlServer: false` on the SQL Server and Polecat registrations now keeps `GuidV7`, the new
  default, instead of `NewId`.

To keep the 6.x ids, call `opts.RestoreV6Defaults()` or set `opts.EnvelopeIdGeneration = EnvelopeIdGeneration.NewId`.
An explicit setting always wins, over the default and over what the message stores prefer.

### Agent command compression, batch grouping and native-ack idempotency are on in 7.0

Three core defaults that were opt-in through 6.x are on in 7.0 (GH-5200). `RestoreV6Defaults()` turns all three off
again, and an explicit setting wins either way.

**Agent commands are compressed.** `opts.Durability.CompressAgentCommands` is `true`, so the leader's agent commands
(lists of agent URIs, often thousands long) are gzipped under the `binary/wolverine+gzip` content type. Every node has
been able to read that content type since 6.44, whatever the setting, so the one rule is:

::: warning Every node must run 6.44 or later before the first 7.0 node starts
A node older than 6.44 doesn't know the compressed content type. It rejects the command rather than misreading it, and
it stops receiving agent assignments until it is upgraded. If a rolling deploy has to go straight from an older 6.x to
7.0, set `opts.Durability.CompressAgentCommands = false` until every node is on 7.0, then remove the setting.
:::

CritterWatch's agent doesn't use this wire path and isn't affected.

**Batches in a partitioned topology are grouped by group id.** When the element type of `BatchMessagesOf<T>()` belongs
to a partitioned topology (`GlobalPartitioned`, `PublishToPartitionedLocalMessaging` or a sharded listener topology),
Wolverine now calls `GroupByGroupId()` for you. Without it a batch envelope carries no group id, lands on a random
slot, and silently gives up the ordering guarantee the unbatched messages of the same type keep. 6.25 already did this
when the batch executed on the topology's local slots; 7.0 also does it when you named the execution queue, called
`ExecuteOnDedicatedLocalQueue()`, or the topology runs over an external transport. Batching outside a partitioned
topology is unchanged, and so is a batch with your own `IMessageBatcher`. To keep the tenant-only batcher, call
`batching.DoNotGroupByGroupId()`. A batch that executes on the topology's slots is still grouped, because it can only be
placed on a slot when it belongs to one group.

**`ProcessInParallelWithNativeAcks()` listeners get the in-memory idempotency guard.** A native-ack listener holds
deliveries unacknowledged, so a restart or rolling deploy redelivers up to the broker's prefetch depth. That is the
case `WithInMemoryIdempotency()` was built for, so it is now on for those listeners with the default 5 minute window
and 100,000 id ceiling. Call `WithInMemoryIdempotency(window, maxTracked)` to change those, or
`WithoutInMemoryIdempotency()` to turn the guard off on one listener, or
`opts.Policies.AllListeners(x => x.WithoutInMemoryIdempotency())` for every listener. For the slot listeners a
partitioned topology creates for you, use `topology.ConfigureListening(x => x.WithoutInMemoryIdempotency())` or the
`AllListeners` policy. Buffered and inline listeners still have no guard
unless they ask for one, and durable listeners keep deduplicating through the inbox.

### Dead and obsolete flags removed (BREAKING)

7.0 deletes a handful of settings and types that were `[Obsolete]`, or that nothing in Wolverine read any more
(GH-5198). They were removed rather than changed, so there's no default for `RestoreV6Defaults()` to put back. If your
code used one of them, it won't compile against 7.0 until you make the change below:

| Removed | What to do instead |
|---|---|
| `opts.UseAutomaticForwarderDiscovery()` | Register each `IForwardsTo<T>` type with `opts.RegisterMessageForwarder<TFrom, TTo>()` or `opts.RegisterMessageForwarder<TFrom>()`. See [`IForwardsTo<T>` discovery is now explicit](#iforwardsto-discovery-is-now-explicit-breaking) |
| `WolverineOptions.EnableMessageCausationTracking` | `opts.Tracking.EnableMessageCausationTracking`. The old property just forwarded to it |
| `opts.Policies.PublishAgentEvents` | Delete the line. Nothing read it |
| `AgentAssignmentsChanged` | Nothing published it after GH-5078. Wrap the runtime's `Observer` and handle `IWolverineObserver.AssignmentsChanged(AssignmentGrid, AgentCommands)` instead |
| `ChainDeduplicationExtensions.ApplyDeduplication(this IChain)` | Call `ApplyDeduplication(rules, container)`, so a store that can write the deduplication claim inside its own transaction gets to |
| `FisherIntegration.TransportSchemaName` | `FisherIntegration.PrefixName`. In 6.x setting `TransportSchemaName` set `PrefixName`, so set `PrefixName` to the same value to keep your queue table names |
| `MqttEnvelope.ShouldRequeue` | Delete it. Nothing read it, so setting it never requeued anything |

### `MessageIdentity` defaults to `IdAndDestination` in 7.0 (schema change)

::: warning
This default changes your database schema. On first start, Wolverine 7 migrates the primary key of the inbox table
(`wolverine_incoming_envelopes`) from `id` to `id, received_at` on PostgreSQL, SQL Server, MySQL, Oracle and SQLite, and
the primary key of the dead letter table (`wolverine_dead_letters`) in the same way where that store keys it by
identity. RavenDB and Cosmos DB put the identity in the document id instead. Plan the upgrade accordingly, or keep the
6.x identity as shown at the end of this section.
:::

Wolverine 7 gives every handler type of a message type its own handler chain, often on a listening endpoint of its own.
That's what makes Wolverine friendlier to modular monoliths, where several modules handle the same message
independently. When a message broker delivers one message to several of those endpoints in the same process (a topic
with one subscription per module, an exchange bound to one queue per module, or an upstream system publishing to both),
every copy arrives with the same envelope id. Under 6.x's `MessageIdentity.IdOnly`, the durable inbox treated every copy
after the first as a duplicate, and the other modules never handled the message.

So 7.0 defaults `DurabilitySettings.MessageIdentity` to `MessageIdentity.IdAndDestination` (GH-5139), which identifies an
inbox row by its envelope id **and** the endpoint that received it.

What to watch for on the upgrade:

* **The schema change.** With the default `AutoBuildMessageStorageOnStartup`, Wolverine applies the new primary key on
  first start. If you manage the schema yourself, generate the migration with the `db-patch` command
  (`db-assert` tells you whether one is outstanding) and apply it before deploying. Changing a primary key rewrites the
  index under a lock, which is quick for a typical inbox but not for a large one.
* **Backfill NULL `received_at` values first.** 6.x writes a NULL `received_at` for a dead letter whose envelope had no
  destination, such as a locally published message that failed before it was routed. A primary key column can't hold
  NULL, so the migration fails on a database that has any of these rows, and the application doesn't start (PostgreSQL
  reports `23502: column "received_at" ... contains null values`). Before upgrading, give them the value Wolverine 7
  writes for a destination-less dead letter, and which the dead letter tooling already shows for them:

  ```sql
  update wolverine.wolverine_dead_letters set received_at = 'unknown://none' where received_at is null;
  update wolverine.wolverine_incoming_envelopes set received_at = 'unknown://none' where received_at is null;
  ```

  Use your own message storage schema name in place of `wolverine`, and run it against every database that holds
  Wolverine envelope storage, including each tenant database.
* **Drain the inbox first.** Upgrade with the inbox drained, so no message is in flight under the old identity. On
  RavenDB and Cosmos DB this matters most: their inbox documents are named by the identity, and a document written under
  the old identity is not found under the new one.
* **Duplicate detection is per endpoint.** The same envelope id arriving at two *different* endpoints is now handled at
  both. The same id arriving twice at the *same* endpoint is still a duplicate.
* **A startup warning guards `IdOnly`.** If you keep `IdOnly` and a message type has handler chains bound to more than
  one durable listening endpoint of an external transport, Wolverine logs a warning at startup naming the message types
  and endpoints. If your broker can deliver one message to more than one of those endpoints, that's exactly the setup
  where the inbox would drop messages.

To keep the 6.x identity, and the 6.x schema, call `opts.RestoreV6Defaults()` or set
`opts.Durability.MessageIdentity = MessageIdentity.IdOnly`. An explicit setting always wins.

### `.NET 9.0` support dropped (BREAKING)

Wolverine 7.0 targets **net10.0 and net11.0**. Move your application to .NET 10 or later, or stay on the latest
Wolverine 6.x release, which still targets net9.0.

Wolverine's own background services (the heartbeat publisher, the claim check sweeper and the broker dead letter queue
listeners) catch everything except cancellation, so none of them faults. .NET 11's `IHost.RunAsync()` and
`IHost.StopAsync()` now throw when *any* `BackgroundService` in the host has faulted, so a faulted service of your own
will surface there where earlier frameworks swallowed it; Wolverine still stops cleanly either way.

## Key Changes in 6.0

::: warning
6.0 is currently in active development on the `main` branch. Items below describe the
state of `main` as the 6.0 work proceeds — the released 6.0 may add to or refine this
list. Bug fixes for the 5.x line continue to ship from the [`5.0` maintenance branch](https://github.com/JasperFx/wolverine/tree/5.0).
:::

### At a glance: settings & APIs that changed in 6.0

The table below is the **complete inventory** of changed defaults, removed APIs, and moved namespaces in 6.0. Anything not in this table is either backward-compatible (new property added with a sensible default) or an internal change with no observable surface. Each row links into the long-form explanation below; the `Migration action` column is the one-line summary if you only have time to skim.

| Setting / API | 5.x | 6.0 | Migration action |
|---|---|---|---|
| `WolverineOptions.ServiceLocationPolicy` | `AllowedButWarn` | **`NotAllowed`** *(BREAKING)* | Restructure registrations or [allow-list per type](#servicelocationpolicy-notallowed-is-the-default-breaking); or set `ServiceLocationPolicy.AllowedButWarn` (`opts.RestoreV5Defaults()` did this in 6.x and was removed in 7.0) |
| `WolverineOptions.UseNewtonsoftForSerialization(...)` | instance method on core `WolverineFx` | extension method in `WolverineFx.Newtonsoft` *(BREAKING)* | Install `WolverineFx.Newtonsoft`; add `using Wolverine.Newtonsoft;` |
| `IEndpointConfiguration<T>.CustomNewtonsoftJsonSerialization(...)` | instance method on the interface | extension method in `WolverineFx.Newtonsoft` *(BREAKING)* | Same |
| `IMassTransitInterop.UseNewtonsoftForSerialization(...)` | interface method | extension method in `WolverineFx.Newtonsoft` *(BREAKING)* | Same |
| `NewtonsoftSerializer` type | `Wolverine.Runtime.Serialization` namespace | `Wolverine.Newtonsoft` namespace *(BREAKING)* | Update `using` |
| `Subscription.Scope` JSON converter attribute | `[StringEnumConverter]` (Newtonsoft) | `[JsonStringEnumConverter]` (STJ) | Wire format unchanged; if you serialize `Subscription` yourself with Newtonsoft and rely on string-named scopes, configure `StringEnumConverter` on your own settings |
| `WolverineHttpOptions.UseNewtonsoftJsonForSerialization(...)` | instance method on core `WolverineFx.Http` | extension method in `WolverineFx.Http.Newtonsoft` *(BREAKING)* | Install `WolverineFx.Http.Newtonsoft`; call `services.AddWolverineHttpNewtonsoft();` alongside `AddWolverineHttp()`; add `using Wolverine.Http.Newtonsoft;` |
| `NewtonsoftHttpSerialization` type | `Wolverine.Http` namespace in core `WolverineFx.Http` | `Wolverine.Http.Newtonsoft` namespace in `WolverineFx.Http.Newtonsoft` *(BREAKING)* | Update `using` |
| `ProblemDetailsContinuationPolicy.WriteProblems` diagnostic logger dump | `JsonConvert.SerializeObject` (Newtonsoft) | `JsonSerializer.Serialize` (STJ) | None — diagnostic-only log line; output shape unchanged |
| `SnapshotLifecycle` namespace | `Marten.Events.Projections` | `JasperFx.Events.Projections` *(BREAKING)* | Add `using JasperFx.Events.Projections;` |
| `OperationRole` namespace | `Marten.Internal.Operations` | `Weasel.Core` *(BREAKING)* | Add `using Weasel.Core;` |
| `DatabaseSagaSchema<,>` / `OracleSagaSchema<,>` namespace | `Wolverine.<Store>.Sagas`, and **the global namespace** for PostgreSQL | `Wolverine.<Store>.Internals` *(BREAKING)* | Add `using Wolverine.<Store>.Internals;`. These are "publinternal" — public only so generated code can name them — and are not a supported API |
| Lightweight relational saga serialization | `JsonSerializerOptions.Default`, not configurable | configurable via `UseSagaSerializerOptions(...)` | None unless publishing Native AOT, where it is now **required** — see [below](#sagas-work-under-native-aot) |
| Target framework | `net8.0;net9.0;net10.0` | `net9.0;net10.0` *(BREAKING)* | Move to .NET 9+ or pin Wolverine 5.x |
| Critter-stack package versions | 1.x line | 2.0-alpha line | Bump in lockstep across JasperFx, Marten, Polecat — full table below |
| `IForwardsTo<T>` discovery | implicit assembly scan at startup | **explicit `opts.RegisterMessageForwarder<TFrom, TTo>()`** *(BREAKING)* | Register each forwarder explicitly; in 6.x you could temporarily call [`opts.UseAutomaticForwarderDiscovery()`](#iforwardsto-discovery-is-now-explicit-breaking) (`[Obsolete]`, removed in 7.0) |
| `MartenConfigurationExpression.EventForwardingToWolverine(...)` | extension method on the Marten configuration expression *(both overloads `[Obsolete]` since 3.x)* | **removed** *(BREAKING)* | Move the flag into the `IntegrateWithWolverine` callback: [`IntegrateWithWolverine(x => x.UseFastEventForwarding = true)`](#eventforwardingtowolverine-removed-breaking) |
| `RedisTransport.BuildRedisStreamUri(...)` | static helper on `RedisTransport` *(`[Obsolete]`)* | **removed** *(BREAKING)* | Call [`RedisEndpointUri.Stream(...)`](#redistransport-buildredisstreamuri-removed-breaking) — identical signature, drop the helper |
| `PulsarEndpoint.UriFor(...)` | static helpers on `PulsarEndpoint` *(both overloads `[Obsolete]`)* | **removed** *(BREAKING)* | For the Wolverine-URI form, call [`PulsarEndpointUri.Topic(...)`](#pulsarendpoint-urifor-removed-breaking); the native-topic-path form (`persistent://...`) was an internal-only seam now covered by `PulsarEndpoint.NativeTopicPath` |
| Runtime code generation (Roslyn) | bundled in core `WolverineFx` (via `JasperFx.RuntimeCompiler`) | **moved to the opt-in `WolverineFx.RuntimeCompilation` package** *(BREAKING for `TypeLoadMode.Dynamic`/`Auto`)* | Reference [`WolverineFx.RuntimeCompilation`](#runtime-code-generation-moved-to-the-wolverinefxruntimecompilation-package-breaking) (it auto-registers), or pre-generate with `TypeLoadMode.Static` |
| **One-line full revert** | n/a | [`opts.RestoreV5Defaults()`](#one-line-revert-restorev5defaults-removed-in-7-0) *(removed in 7.0)* | Flip every runtime default this method covers back to its 5.x value |

::: tip
If you just want to adopt the 6.0 API surface and bug fixes without simultaneously adopting the new runtime defaults, call [`opts.RestoreV5Defaults()`](#one-line-revert-restorev5defaults-removed-in-7-0) once at the top of your `UseWolverine` lambda. Then opt into each new default at your own pace.
:::

---

### `.NET 8.0` support dropped (BREAKING)

Wolverine 6.0 requires **.NET 9.0 or .NET 10.0**. The JasperFx 2.0-alpha line that the rest of 6.0 builds on no longer targets net8.0, so neither does Wolverine.

If you're on .NET 8 LTS and aren't ready to move:

- Pin to the latest Wolverine 5.x release.
- Bug fixes for the 5.x line continue to ship from the [`5.0` maintenance branch](https://github.com/JasperFx/wolverine/tree/5.0) — open issues / PRs that target that branch the same way you do for `main`.

### Critter-stack 2.0-alpha dependency line (BREAKING)

The whole critter-stack moved to a coordinated 2.0-alpha set of packages built on JasperFx 2.0. Wolverine 6.0 pins:

| Package | 5.x line | 6.0 |
|---|---|---|
| JasperFx | 1.31.x | 2.0.0-alpha.10 |
| JasperFx.Events | 1.36.x | 2.0.0-alpha.3 |
| JasperFx.RuntimeCompiler | 4.5.x | 2.0.0-alpha.2 |
| JasperFx.SourceGeneration | 1.1.x | 2.0.0-alpha.2 |
| Marten | 8.35.x | 9.0.0-alpha.1 |
| Marten.AspNetCore | 8.35.x | 9.0.0-alpha.1 |
| Polecat | 2.1.x | 4.0.0-alpha.1 |

If you reference any of these directly from your own project, bump in lockstep with Wolverine.

### `SnapshotLifecycle` moved to `JasperFx.Events.Projections` (BREAKING)

`Marten.Events.Projections.SnapshotLifecycle` no longer exists in Marten 9. The same type now lives at `JasperFx.Events.Projections.SnapshotLifecycle` and is consumed by both Marten and Polecat.

If you import `using Marten.Events.Projections;` and reference `SnapshotLifecycle.Inline` / `SnapshotLifecycle.Async`, add `using JasperFx.Events.Projections;` alongside it. Marten's `Snapshot<T>(...)` overload still accepts the type from this new namespace.

There is no Wolverine documentation page for `SnapshotLifecycle` itself — it's a Marten projection-configuration type. The Marten 9 projection docs cover its full semantics; the only Wolverine-visible change is the `using` directive.

### `OperationRole` moved to `Weasel.Core` (BREAKING)

If you write custom `IStorageOperation` implementations (rare but supported), `OperationRole` now lives in `Weasel.Core` instead of `Marten.Internal.Operations`. Add `using Weasel.Core;`.

No Wolverine documentation page covers this directly — `IStorageOperation` is Marten internals territory. The only Wolverine-visible change is the `using` directive.

### Relational saga schema types moved to `Internals` (BREAKING)

The lightweight relational saga schema types moved out of `Wolverine.<Store>.Sagas` and into
`Wolverine.<Store>.Internals`:

| Package | 5.x namespace | 6.0 namespace |
|---|---|---|
| `WolverineFx.Postgresql` | **none — the global namespace** | `Wolverine.Postgresql.Internals` |
| `WolverineFx.SqlServer` | `Wolverine.SqlServer.Sagas` | `Wolverine.SqlServer.Internals` |
| `WolverineFx.Sqlite` | `Wolverine.Sqlite.Sagas` | `Wolverine.Sqlite.Internals` |
| `WolverineFx.MySql` | `Wolverine.MySql.Sagas` | `Wolverine.MySql.Internals` |
| `WolverineFx.Oracle` | `Wolverine.Oracle.Sagas` | `Wolverine.Oracle.Internals` |

The PostgreSQL one is the reason this is worth your attention: its file carried no `namespace`
declaration at all, so a **public** type named `DatabaseSagaSchema<T, TId>` sat in the *global*
namespace of a shipped package — visible unqualified in every file of every project that referenced
`WolverineFx.Postgresql`, and colliding by name with the four sibling packages' versions of the same
type.

These types are *publinternal*: technically public, deliberately not part of the supported surface.
They are public only because Wolverine's generated saga code has to name the closed type in order to
construct it, and a `typeof()` in generated code cannot see an `internal` type. The `Internals`
namespace says so. If you are referencing one directly, you are almost certainly reaching for
something that should be solved another way — but the migration is just a `using`.

### Sagas work under Native AOT

Sagas could not run in a published Native AOT image at all before 6.0, for three separate reasons —
a generic virtual method ILC cannot dispatch, a rooting attribute that preserves metadata without
generating code, and saga JSON serialization with no configurable options. All three are fixed, and
CI now publishes and runs a real native image that drives a saga end to end.

One thing is **required of your application** if you publish Native AOT: `PublishAot` disables
reflection-based `System.Text.Json`, so you have to hand Wolverine a source-generated
`JsonSerializerContext` for your saga state types.

```csharp
[JsonSerializable(typeof(MySaga))]
internal partial class MySagaJsonContext : JsonSerializerContext;

// ...

opts.PersistMessagesWithPostgresql(connectionString)
    .UseSagaSerializerOptions(new JsonSerializerOptions
    {
        TypeInfoResolver = MySagaJsonContext.Default
    });
```

`UseSagaSerializerOptions(...)` is available on all five relational persistence builders and applies
to tenant databases as well as the main one. Leaving it unset preserves the previous behaviour
exactly, so it costs nothing if you are not publishing AOT.

### `ServiceLocationPolicy.NotAllowed` is the default (BREAKING)

In Wolverine 6.0, `WolverineOptions.ServiceLocationPolicy` defaults to `NotAllowed` (was `AllowedButWarn` in 5.x). Apps that previously relied on Wolverine's code generation falling back to service location at runtime now throw `InvalidServiceLocationException` on startup.

**Preferred upgrade path** — change IoC registrations to forms Wolverine can see through:

- `AddScoped<TInterface, TImpl>()` instead of `AddScoped<TInterface>(sp => new TImpl(...))`
- Constructor injection into your handlers (Wolverine inlines that into generated code)

**Escape hatch** — opt specific types into the allow-list:

```csharp
opts.CodeGeneration.AlwaysUseServiceLocationFor<IMyOpaqueService>();
```

Use this for services with genuinely opaque lambda factory registrations (Refit proxies, EF Core `DbContext` types with runtime configuration, etc.). The rest of the codegen stays inlined; only the listed types route through the service locator.

**To prepare while still on 5.x:**

1. Run your app with `Warning`-level (or lower) logging on the `Wolverine` category. Look for log messages starting with `Utilizing service location for ...`.
2. For each one, either restructure the registration (preferred) or opt the type in explicitly with `opts.CodeGeneration.AlwaysUseServiceLocationFor<T>()`.
3. Once the warnings are gone, set `opts.ServiceLocationPolicy = ServiceLocationPolicy.NotAllowed` on 5.x to assert the absence going forward — on 6.0 this is the default, no explicit assignment needed.

See [Code Generation](/guide/codegen.html) for the full IoC + service-location story, including the LOUD callout at the top of that page.

### One-line revert: `RestoreV5Defaults()` (removed in 7.0)

::: warning
`RestoreV5Defaults()` was removed in Wolverine 7.0. Set `opts.ServiceLocationPolicy = ServiceLocationPolicy.AllowedButWarn;` instead, and see [`RestoreV6Defaults()`](#defaults-that-changed-in-7-0-restorev6defaults) for the 7.0 defaults.
:::

For applications that want to adopt Wolverine 6's API surface, NuGet line, target frameworks, and bug fixes — without simultaneously adopting its new runtime defaults — Wolverine 6.0 adds a one-line escape hatch:

```csharp
builder.Host.UseWolverine(opts =>
{
    opts.RestoreV5Defaults();
    // … the rest of your configuration
});
```

`RestoreV5Defaults()` flips every runtime default that changed between Wolverine 5.x and 6.x back to its 5.x value. Today that means **one** default: `ServiceLocationPolicy` flips from `NotAllowed` back to `AllowedButWarn`. As more defaults flip between 6.0 and 6.x patch releases, they get added to this method and to the [at-a-glance table above](#at-a-glance-settings-apis-that-changed-in-6-0).

**What this method does NOT do:**

- **It does not change the default JSON serializer.** System.Text.Json has been the Wolverine default since **5.0** (`UseSystemTextJsonForSerialization()` is wired in the `WolverineOptions` constructor). There is no 5.x Newtonsoft default to restore. If you want Newtonsoft as the default (the 4.x-and-earlier behavior), install [`WolverineFx.Newtonsoft`](#newtonsoft-json-moved-to-wolverinefx-newtonsoft-package-breaking) and call `opts.UseNewtonsoftForSerialization()` explicitly.
- **It does not change build-time things.** Dropping `net8.0` and moving to the JasperFx 2.0-alpha NuGet line are not runtime flags. Either move to net9+ or pin Wolverine 5.x.
- **It does not change package layout.** Newtonsoft moving to its own NuGet package is resolved by `dotnet add package WolverineFx.Newtonsoft`, not by a runtime call.

**Treat it as a temporary migration step.** The point of upgrading to 6.0 is to adopt the new defaults eventually; `RestoreV5Defaults()` exists so you can sequence the migration — adopt 6.0's API + bug fixes today, then remove the call and opt into each new default at your own pace.

### Newtonsoft.Json moved to `WolverineFx.Newtonsoft` package (BREAKING)

The core `WolverineFx` NuGet package no longer depends on Newtonsoft.Json. All Newtonsoft integration moved to a separate `WolverineFx.Newtonsoft` package and is exposed as **extension methods** rather than instance methods on `WolverineOptions` / `IEndpointConfiguration` / `IMassTransitInterop`.

If you call any of the following 5.x APIs:

- `WolverineOptions.UseNewtonsoftForSerialization(...)`
- `IEndpointConfiguration<T>.CustomNewtonsoftJsonSerialization(...)`
- `IMassTransitInterop.UseNewtonsoftForSerialization(...)`
- `new NewtonsoftSerializer(...)` directly

you must:

1. **Install the new package**: `dotnet add package WolverineFx.Newtonsoft`
2. **Add `using Wolverine.Newtonsoft;`** to bring the extension methods (and the `NewtonsoftSerializer` type) into scope.

The call surface itself is unchanged — `opts.UseNewtonsoftForSerialization(settings => …)` etc. work exactly as before, they're just extension methods now. See the [Newtonsoft.Json Serialization](/guide/messages.html#newtonsoft-json-serialization) section of the serialization docs.

Transports that pin a `NewtonsoftSerializer` internally for NServiceBus / MassTransit wire-compat (RabbitMQ's `UseNServiceBusInterop()`, the AWS SQS and SNS NServiceBus mappers, Azure Service Bus listeners) carry the `WolverineFx.Newtonsoft` dependency for you. You don't need to install it explicitly unless you call one of the Newtonsoft APIs from your own code.

In a related cleanup, `Subscription.Scope` no longer carries a `[Newtonsoft.Json.Converters.StringEnumConverter]` attribute — it's now annotated with `[System.Text.Json.Serialization.JsonStringEnumConverter]`. Wire format is unchanged (still string-named scopes). If you serialize `Subscription` instances yourself with Newtonsoft and rely on the string-named scope shape, configure `StringEnumConverter` on your own settings explicitly.

### `Wolverine.Http` Newtonsoft moved to `WolverineFx.Http.Newtonsoft` package (BREAKING)

Mirrors the core extraction above: the `WolverineFx.Http` NuGet package no longer depends on Newtonsoft.Json. All Newtonsoft-flavored HTTP serialization moved to a separate `WolverineFx.Http.Newtonsoft` package and is exposed as an **extension method** rather than an instance method on `WolverineHttpOptions`.

If you call the following 5.x API:

- `WolverineHttpOptions.UseNewtonsoftJsonForSerialization(...)`
- `new NewtonsoftHttpSerialization(...)` directly (rare — it was an internal-feeling singleton)

you must:

1. **Install the new package**: `dotnet add package WolverineFx.Http.Newtonsoft`
2. **Register the package's services** alongside `AddWolverineHttp()`:
   ```csharp
   builder.Services.AddWolverineHttp();
   builder.Services.AddWolverineHttpNewtonsoft();   // new
   ```
3. **Add `using Wolverine.Http.Newtonsoft;`** to bring the extension method (and the `NewtonsoftHttpSerialization` type) into scope.

The call surface itself is unchanged — `opts.UseNewtonsoftJsonForSerialization(settings => …)` works exactly as before, it's just an extension method now. See the [HTTP JSON serialization docs](/guide/http/json.html#using-newtonsoft-json) for the full example.

Core `WolverineFx.Http` retains the `JsonUsage.NewtonsoftJson` enum value so the public switch shape doesn't change. The codegen path inside `JsonResourceWriterPolicy` / `JsonBodyParameterStrategy` now dispatches through an internal `INewtonsoftHttpCodeGen` seam implemented in the new package; selecting `JsonUsage.NewtonsoftJson` without the package installed throws an `InvalidOperationException` at codegen time pointing you at `dotnet add package WolverineFx.Http.Newtonsoft`.

In a related cleanup, `ProblemDetailsContinuationPolicy.WriteProblems` (which dumps the `ProblemDetails` payload into the log line for the "found problems with this message" diagnostic) switched from `Newtonsoft.Json.JsonConvert.SerializeObject` to `System.Text.Json.JsonSerializer.Serialize`. This is a logging dump only — `ProblemDetails` round-trips cleanly through STJ; there is no observable output-format change.

### Runtime code generation moved to the `WolverineFx.RuntimeCompilation` package (BREAKING)

In 5.x, core `WolverineFx` referenced `JasperFx.RuntimeCompiler` (≈100 MB of Roslyn assemblies) and registered a default `IAssemblyGenerator`, so runtime code generation always "just worked." In 6.0 that dependency is **removed from core** and lives only in the opt-in **`WolverineFx.RuntimeCompilation`** package. This lets `TypeLoadMode.Static` / Native-AOT deployments ship without Roslyn — smaller binaries, faster cold start — but it is a breaking change for apps that compile at runtime.

**Who is affected:** any app running `TypeLoadMode.Dynamic` (the default) or `TypeLoadMode.Auto`. Such an app now fails fast at startup with an error telling you to add the package or switch to Static:

> Wolverine is running in TypeLoadMode.Dynamic, which compiles handler/middleware code at runtime, but no IAssemblyGenerator (Roslyn) is registered. Core WolverineFx no longer ships the runtime compiler. Either add the 'WolverineFx.RuntimeCompilation' NuGet package (it auto-registers when referenced, or call opts.UseRuntimeCompilation() in UseWolverine(...)), or pre-generate code with 'dotnet run -- codegen write' and set opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Static.

**Migration — pick one:**

1. **Keep runtime codegen (simplest).** Reference the package; that's all — its `[WolverineModule]` auto-registers the Roslyn `IAssemblyGenerator` at bootstrap:
   ```sh
   dotnet add package WolverineFx.RuntimeCompilation
   ```
   If you've disabled extension auto-discovery (`ExtensionDiscovery.ManualOnly`), the module won't load automatically — call `opts.UseRuntimeCompilation()` inside `UseWolverine(...)` instead.

2. **Go Roslyn-free in production (recommended for AOT / fast cold start).** Pre-generate code with `dotnet run -- codegen write`, commit `Internal/Generated`, and set `opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Static`. Reference `WolverineFx.RuntimeCompilation` only as a dev/build-time dependency (for `codegen write`); production ships without Roslyn. See the [Code Generation guide](/guide/codegen).

`WolverineFx.RuntimeCompilation` is available from `6.0.0-rc.2` onward.

### `IForwardsTo<T>` discovery is now explicit (BREAKING)

Wolverine 5.x scanned the application assembly at handler-graph compile time for every concrete type that implemented `IForwardsTo<T>` and wired the source-type → target-type forwarding automatically. That scan walks `Assembly.ExportedTypes` and is **not trim/AOT-clean** — so 6.0 removes it from the default startup path as part of the [AOT pillar](https://github.com/JasperFx/wolverine/issues/2746). Any application that relied on the implicit scan needs to opt in to one of two paths:

**Preferred upgrade path** — register each `IForwardsTo<T>` pair explicitly in your `UseWolverine` lambda:

```csharp
opts.RegisterMessageForwarder<PersonBorn, PersonBornV2>();
```

There's also a single-type-argument overload that derives the target type from the `IForwardsTo<>` closure (handy for attribute-driven helpers):

```csharp
opts.RegisterMessageForwarder<PersonBorn>(); // resolves to <PersonBorn, PersonBornV2>
```

Both forms feed `WolverineOptions.ExplicitMessageForwarders`, which is drained inside `HandlerGraph.Compile` — exactly where `Forwarders.FindForwards(Assembly)` used to run. The on-the-wire dispatch behavior (the older message type's `Transform()` builds the newer message, the newer message's handler runs) is unchanged.

**Escape hatch (6.x only) — opt back into the legacy scan:**

```csharp
opts.UseAutomaticForwarderDiscovery();
```

This re-enables the 5.x assembly scan against `WolverineOptions.ApplicationAssembly`. It is marked `[Obsolete]` and `[RequiresUnreferencedCode]` because the underlying scan walks exported types and the trimmer cannot prove which forwarder types are reachable. It was a backward-compatibility escape valve for the 6.0 release cycle and **was removed in 7.0** — see [obsolete flags removed in 7.0](#dead-and-obsolete-flags-removed-breaking).

If you don't use `IForwardsTo<T>` at all, no change is needed.

### `EventForwardingToWolverine()` removed (BREAKING)

Both `MartenConfigurationExpression.EventForwardingToWolverine(...)` extension method overloads (parameterless and the one taking `Action<IEventForwarding>`) were marked `[Obsolete]` in the 3.x line with a "will be removed in Wolverine 4.0" notice. They are now actually removed in 6.0.

The replacement has been available since 3.0: set `MartenIntegration.UseFastEventForwarding = true` inside the `IntegrateWithWolverine` configure callback. Event-subscription transformations (`SubscribeToEvent<T>().TransformedTo(...)`) live on the same `MartenIntegration` instance, so they move into the same callback.

**Before (5.x):**

```csharp
services.AddMarten(opts => opts.Connection(connString))
    .IntegrateWithWolverine()
    .EventForwardingToWolverine();
```

**After (6.0):**

```csharp
services.AddMarten(opts => opts.Connection(connString))
    .IntegrateWithWolverine(x => x.UseFastEventForwarding = true);
```

The `Action<IEventForwarding>` overload migrates to the same callback:

```csharp
services.AddMarten(opts => opts.Connection(connString))
    .IntegrateWithWolverine(x =>
    {
        x.UseFastEventForwarding = true;
        x.SubscribeToEvent<SecondEvent>()
            .TransformedTo(e => new SecondMessage(e.StreamId, e.Sequence));
    });
```

### `RedisTransport.BuildRedisStreamUri(...)` removed (BREAKING)

Both `RedisTransport.BuildRedisStreamUri(...)` overloads (the 2-param `(streamKey, databaseId)` form and the 3-param `(streamKey, databaseId, consumerGroup)` form) were marked `[Obsolete]` and were thin forwarders to `RedisEndpointUri.Stream(...)`. They're now removed in 6.0.

Replace any call to `RedisTransport.BuildRedisStreamUri(...)` with the identical-signature `RedisEndpointUri.Stream(...)`:

**Before:**

```csharp
var uri = RedisTransport.BuildRedisStreamUri("orders", databaseId: 3);
```

**After:**

```csharp
var uri = RedisEndpointUri.Stream("orders", databaseId: 3);
```

If you never called these helpers directly (most users don't — Wolverine builds these URIs internally from `ListenToRedisStream(...)` / `PublishToRedisStream(...)`), no change is needed.
### `PulsarEndpoint.UriFor(...)` removed (BREAKING)

Both `PulsarEndpoint.UriFor(...)` overloads were `[Obsolete]` in the 5.x line and are now removed in 6.0:

- **`UriFor(string topicPath)`** — was a thin forwarder to `PulsarEndpointUri.Topic(topicPath)`. Replace any caller with `PulsarEndpointUri.Topic(...)` directly. Same signature, same return shape.

- **`UriFor(bool persistent, string tenant, string @namespace, string topicName)`** — returned a Pulsar-*native* topic path of the form `persistent://{tenant}/{ns}/{topic}` for hand-off to the native Pulsar client. That is NOT a Wolverine endpoint URI (`pulsar://...`); the two forms are deliberately not interchangeable. The internal Pulsar transport now uses a renamed helper `PulsarEndpoint.NativeTopicPath(...)` (also `internal`, same signature). External callers of the old `UriFor(bool, ...)` overload were rare-to-nonexistent — if you need this form for your own native-Pulsar interop, construct it directly:

  ```csharp
  var scheme = persistent ? "persistent" : "non-persistent";
  var nativePath = new Uri($"{scheme}://{tenant}/{@namespace}/{topicName}");
  ```

If you only ever called `UriFor(string)`, this is a one-line `using`-already-imported rename.

### Performance: per-endpoint serializer cache pre-population

The `Endpoint._serializers` cache that used to fill on first-miss at runtime is now pre-populated during `Endpoint.Compile()` with every globally-registered serializer. Effect for users: zero behavioral change; the hot-path serializer lookup is now a pure read.

Side note for users who register an `IMessageSerializer` *after* host startup: the post-Compile registration is still picked up via fallback to the global serializer registry on miss, but it's no longer cached on the endpoint. Either register the serializer before `StartAsync()` (the documented path) or use the endpoint-level `RegisterSerializer(...)` API on the specific endpoint that should see it.

### Performance conventions for contributors

Wolverine's hot-path dictionary lookups stay on `ImHashMap<TKey, TValue>` — this is now codified in [the contributors' CLAUDE.md](https://github.com/JasperFx/wolverine/blob/main/CLAUDE.md#performance-conventions). If you're contributing a perf PR and feel the urge to swap `ImHashMap` for `FrozenDictionary`, read that section first. The right fix for hot-path mutation is bootstrap-time pre-population in the relevant `Compile()` path, not a data-structure swap.

### Stale `DefaultSerializer` XmlDoc fixed

A long-standing XmlDoc on `WolverineOptions.DefaultSerializer` claimed Newtonsoft.Json as the default. System.Text.Json has actually been the default since Wolverine **5.0** (`UseSystemTextJsonForSerialization()` is wired in the default constructor). The XmlDoc was finally corrected in 6.0; no behavior change.

## Key Changes in 5.0

5.0 had very few breaking changes in the public API, but some in "publinternals" types most users would never touch. The
biggest change in the internals is the replacement of the venerable [TPL DataFlow library](https://learn.microsoft.com/en-us/dotnet/standard/parallel-programming/dataflow-task-parallel-library) 
with the [System.Threading.Channels library](https://learn.microsoft.com/en-us/dotnet/core/extensions/channels)
in every place that Wolverine uses in memory queueing. The only change this caused to the public API was the removal of
the option for direct configuration of the TPL DataFlow `ExecutionOptions`. Endpoint ordering and parallelization options
are unchanged otherwise in the public fluent interface for configuration. 

The `IntegrateWithWolverine()` syntax for ["ancillary stores"](/guide/durability/marten/ancillary-stores) changed to a [nested closure](https://martinfowler.com/dslCatalog/nestedClosure.html) syntax to be more consistent
with the syntax for the main [Marten](https://martendb.io) store. The [Wolverine managed distribution of Marten projections and subscriptions](/guide/durability/marten/distribution)
now applies to the ancillary stores as well. 

The new [Partitioned Sequential Messaging](/guide/messaging/partitioning) feature is a potentially huge step forward for
building a Wolverine system that can efficiently and resiliently handle concurrent access to sensitive resources.

The [Aggregate Handler Workflow](/guide/durability/marten/event-sourcing) feature with Marten now supports strong typed identifiers.

The declarative data access features with Marten (`[Aggregate]`, `[ReadAggregate]`, `[Entity]` or `[Document]`) can utilize
Marten batch querying for better efficiency when a handler or HTTP endpoint uses more than one declaration for data loading.

Better control over how [Wolverine generates code with respect to IoC container usage](/guide/codegen.html#wolverine-code-generation-and-ioc).

`IServiceContainer` moved to the `JasperFx` namespace.

By and large, we've *tried* to replace any API nomenclature using "master" with "main."

## Key Changes in 4.0

* Wolverine dropped all support for .NET 6/7
* The previous dependencies on Oakton, JasperFx.Core, and JasperFx.CodeGeneration were all combined into a single [JasperFx](https://github.com/jasperfx/jasperfx) library. There are shims for any method with "Oakton" in its name, but these are marked as `[Obsolete]`. You can pretty well do a find and replace for "Oakton" to "JasperFx". If your Oakton command classes live in a different project than the runnable application, add this to that project's `Properties/AssemblyInfo.cs` file:
  ```cs
  using JasperFx;

  [assembly: JasperFxAssembly]
  ```
  This attribute replaces the older Oakton assembly attribute:
  ```cs
  using Oakton;

  [assembly: OaktonCommandAssembly]
  ```
* Internally, the full "Critter Stack" is trying to use `Uri` values to identify databases when targeting multiple databases in either a modular monolith approach or with multi-tenancy
* Many of the internal dependencies like Marten or AWS SQS SDK Nugets were updated
* The signature of the Kafka `IKafkaEnvelopeMapper` changed somewhat to be more efficient in message serialization
* Wolverine now supports [multi-tenancy through separate databases for EF Core](/guide/durability/efcore/multi-tenancy)
* The Open Telemetry span names for executing a message are now the [Wolverine message type name](/guide/messages.html#message-type-name-or-alias)

## Key Changes in 3.0

The 3.0 release did not have any breaking changes to the public API, but does come with some significant internal
changes.

### Built-in IoC Container

The biggest change is that Wolverine no longer automatically replaces the built in `ServiceProvider` and
uses the .NET conforming container directly. At this point it is theoretically possible to use Wolverine with
any IoC library that fully supports the ASP.Net Core DI conformance behavior, but Wolverine has only been
tested against the default `ServiceProvider`.

Do be aware that `ServiceProvider` is stricter than some third-party containers about service resolution and
lifetime scoping, so there might be some hiccups. See the [Configuration Guide](/guide/configuration) for more
information. Wolverine uses the IoC configuration to generate code that inlines dependency creation in a way
that's more efficient than resolving through an IoC tool at runtime when it can.

::: tip
If you have any issues with Wolverine's code generation about your message handlers or HTTP endpoints after
upgrading, please open a GitHub issue with Wolverine.
:::

Wolverine 3.0 can now be bootstrapped with the `HostApplicationBuilder` or any standard .NET bootstrapping mechanism through
`IServiceCollection.AddWolverine()`. The limitation of having to use `IHostBuilder` is gone.

### Marten Integration

The Marten/Wolverine `IntegrateWithWolverine()` integration syntax changed from a *lot* of optional arguments to a single
call with a nested lambda registration like this:

<!-- snippet: sample_using_integrate_with_wolverine_with_multiple_options -->
<a id='snippet-sample_using_integrate_with_wolverine_with_multiple_options'></a>
```cs
services.AddMarten(opts =>
    {
        opts.Connection(Servers.PostgresConnectionString);
        opts.DisableNpgsqlLogging = true;
    })
    .IntegrateWithWolverine(w =>
    {
        w.MessageStorageSchemaName = "public";
        w.TransportSchemaName = "public";
    })
    .ApplyAllDatabaseChangesOnStartup();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/DuplicateMessageSending/Program.cs#L50-L63' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_integrate_with_wolverine_with_multiple_options' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

All Marten/Wolverine integration options are available by this one syntax call now, with the exception of event subscriptions.

### Wolverine.RabbitMq

The RabbitMq transport received a significant overhaul for 3.0.

#### RabbitMq Client v7

The RabbitMq .NET client has been updated to v7, bringing with it an internal rewrite to support async I/O and vastly improved memory usage & throughput. This version also supports OTEL out of the box.

::: warning
RabbitMq v7 is newly released. If you use another RabbitMQ wrapper/bus in your application, hold off on upgrading until it also supports v7.
:::

#### Conventional Routing Improvements
- Queue bindings can now be manually overridden on a per-message basis via `BindToExchange`, this is useful for scenarios where you wish to use conventional naming between different applications but need other exchange types apart from `FanOut`. This should make conventional routing the default usage in the majority of situations. See [Conventional Routing](/guide/messaging/transports/rabbitmq/conventional-routing) for more information.
- Conventional routing entity creation has been split between the sender and receive side. Previously the sender would generate all exchange and queue bindings, but now if the sender has no handlers for a specific message, the queues will not be created.

#### General RabbitMQ Improvements
- Added support for Headers exchange
- Queues now apply bindings instead of exchanges. This is an internal change and shouldn't result in any obvious differences for users.
- The configuration model has expanded flexibility with Queues now bindable to Exchanges, alongside the existing model of Exchanges binding to Queues.
- The previous `BindExchange()` syntax was renamed to `DeclareExchange()` to better reflect Rabbit MQ operations

### Sagas

Wolverine 3.0 added optimistic concurrency support to the stateful `Saga` support. This will potentially cause database
migrations for any Marten-backed `Saga` types as it will now require the numeric version storage.

### Leader Election

The leader election functionality in Wolverine has been largely rewritten and *should* eliminate the issues with poor 
behavior in clusters or local debugging time usage where nodes do not gracefully shut down. Internal testing has shown
a significant improvement in Wolverine's ability to detect node changes and rollover the leadership election.

### Wolverine.PostgresSql

The PostgreSQL transport option requires you to explicitly set the `transportSchema`, or Wolverine will fall through to
using `wolverine_queues` as the schema for the database backed queues. Wolverine will no longer use the envelope storage
schema for the queues.

### Wolverine.Http

For [Wolverine.Http usage](/guide/http/), the Wolverine 3.0 usage of the built in `ServiceProvider`
necessitates the usage of this API to register necessary services for Wolverine.HTTP in addition to
adding the Wolverine endpoints:

<!-- snippet: sample_adding_http_services -->
<a id='snippet-sample_adding_http_services'></a>
```cs
var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

// Necessary services for Wolverine HTTP
// And don't worry, if you forget this, Wolverine
// will assert this is missing on startup:(
builder.Services.AddWolverineHttp();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Program.cs#L44-L54' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_adding_http_services' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Also for Wolverine.Http users, the `[Document]` attribute behavior in the Marten integration is now "required by default."

### Azure Service Bus

The Azure Service Bus will now "sanitize" any queue/subscription names to be all lower case. This may impact your usage of
conventional routing. Please report any problems with this to GitHub.

### Messaging

The behavior of `IMessageBus.InvokeAsync<T>(message)` changed in 3.0 such that the `T` response **is not also published as a 
message** at the same time when the initial message is sent with request/response semantics. Wolverine has gone back and forth
in this behavior in its life, but at this point, the Wolverine thinks that this is the least confusing behavioral rule. 

You can selectively override this behavior and tell Wolverine to publish the response as a message no matter what
by using the new 3.0 `[AlwaysPublishResponse]` attribute like this:

<!-- snippet: sample_using_alwayspublishresponse -->
<a id='snippet-sample_using_alwayspublishresponse'></a>
```cs
public class CreateItemCommandHandler
{
    // Using this attribute will force Wolverine to also publish the ItemCreated event even if
    // this is called by IMessageBus.InvokeAsync<ItemCreated>()
    [AlwaysPublishResponse]
    public async Task<(ItemCreated, SecondItemCreated)> Handle(CreateItemCommand command, IDocumentSession session)
    {
        var item = new Item
        {
            Id = Guid.NewGuid(),
            Name = command.Name
        };

        session.Store(item);

        return (new ItemCreated(item.Id, item.Name), new SecondItemCreated(item.Id, item.Name));
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/MartenTests/Bugs/Bug_305_invoke_async_with_return_not_publishing_with_tuple_return_value.cs#L77-L97' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_alwayspublishresponse' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->
