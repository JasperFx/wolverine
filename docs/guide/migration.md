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
| `KafkaProducerOptions.Acks` below `All` | accepted (librdkafka producers were not idempotent by default) | **startup fails** unless idempotence is off *(BREAKING)* | Call `DisableIdempotentProducer()` (or set `EnableIdempotence = false`) alongside the acks, or use `Acks.All`; `RestoreV6Defaults()` also turns idempotence off. See [Kafka behaviour changes](#kafka-behaviour-changes) |
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
| `INodeAgentPersistence.HasLeadershipLock()` | synchronous, pinged the lock's database session with blocking I/O | `HasLeadershipLockAsync(CancellationToken)` *(BREAKING for custom node persistence)* | See [leadership lock probe is asynchronous](#the-leadership-lock-probe-is-asynchronous-breaking) |
| Several handler types for one message type | combined into one chain and one transaction by default (`ClassicCombineIntoOneLogicalHandler`) | **one chain per handler type**, each with its own transaction, local queue and generated type *(BREAKING)* | See [one handler chain per handler type](#one-handler-chain-per-handler-type-breaking); regenerate pre-generated code |
| `WolverineOptions.MultipleHandlerBehavior`, the `MultipleHandlerBehavior` enum, `AssertNoBatchHandlerConflicts()` | `ClassicCombineIntoOneLogicalHandler` (default, `[Obsolete]` in 6.52) or `Separated` | **removed**: 7.0 always behaves like `Separated` *(BREAKING)* | Delete the setting and the call; `AssertNoCombinedHandlers()` is an `[Obsolete]` no-op for one major version |
| `InvokeAsync(msg)` for a message type with several handler types | ran the one combined chain in one transaction | runs each chain in turn, each in its own transaction; the first failure throws `HandlerInvocationException` *(BREAKING)* | See [InvokeAsync with several handler types](#invokeasync-and-streamasync-with-several-handler-types) |
| `InvokeAsync<T>` / `StreamAsync<T>` for such a message type | ran the combined chain | run only the chain that returns `T`; none or several throw `AmbiguousHandlerChainException` *(BREAKING)* | Make exactly one handler type return `T` |
| `LocalQueueFor<T>()` / `[LocalQueue]` on a message type with several handler types | the message type's queue received the message | the queue only drains rows left over from 6.x; each handler type's own queue receives the message *(BREAKING)* | Move the queue's settings to the handler types' queues |
| New database queue tables (PostgreSQL, SQL Server, MySQL, Oracle, SQLite transports) | no ordering column; SQL Server classic layout | deterministic ordering, and SQL Server's throughput layout, **for new tables only** | None for existing tables; see [queue tables](#new-queue-tables-get-deterministic-ordering-and-sql-server-s-throughput-layout) |
| `WolverineFx.MQTT` client library | MQTTnet 4 + `MQTTnet.Extensions.ManagedClient` | **MQTTnet 5** *(BREAKING for code using MQTTnet types directly)* | See [MQTT runs on MQTTnet 5](#mqtt-transport-runs-on-mqttnet-5-wolverinefx-mqtt5-retired-breaking) |
| `WolverineFx.Mqtt5` package | MQTTnet 5 flavor of the MQTT transport | **retired**; last published in 6.x *(BREAKING)* | Reference `WolverineFx.MQTT` instead; nothing else changes |
| Per-store `[DocumentExists<T>]` / `[DocumentDoesNotExist<T>]`, `IEventForwarding`, `EventForwardingTransform<T>` (Marten, Polecat, Fisher) | one copy per store | **`[Obsolete]`**: one core type each, same names; removed in 8.0 | See [obsolete in 7.0](#obsolete-in-7-0) |
| JasperFx / Weasel / Marten / Polecat / Fisher | 2.x / 9.x / 9.x / 5.x / 1.x | 3.0 / 10.0 / 10.0 / 6.0 / 2.0 *(BREAKING)* | Upgrade them together; see [dependency majors](#dependency-major-versions-in-7-0-breaking) |
| Wolverine's OpenTelemetry `Meter` name | `Wolverine:{ServiceName}`, so only `AddMeter("Wolverine*")` collected it | exactly `Wolverine` *(BREAKING for exact-name meter filters)* | `AddMeter("Wolverine")` now works; `AddMeter("Wolverine*")` keeps working. See [meter and metric names](#opentelemetry-meter-and-metric-names-breaking) |
| Wolverine metric instrument names | dashed (`wolverine-messages-sent`) | dotted (`wolverine.messages.sent`) *(BREAKING for dashboards and alerts)* | Rename the series in dashboards, alerts and recording rules; see [the full table](#opentelemetry-meter-and-metric-names-breaking) |
| `WolverineOptions.ServiceName` default | the entry assembly name | `OTEL_SERVICE_NAME` when set, then the entry assembly name | None, unless a service relied on the assembly name while `OTEL_SERVICE_NAME` was set to something else: set `opts.ServiceName` explicitly |
| `Saga.LastModified` | not there | new `DateTimeOffset` property on the base `Saga`, set to UTC now by every saga store on insert and update *(schema change for EF Core sagas)* | EF Core: add a migration for the new column or `Ignore()` it. Nothing else; see [saga LastModified](#saga-lastmodified-is-set-by-every-saga-store) |

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

- **Producers are idempotent by default, and acks are checked at startup.** Dekaf producers are idempotent unless `EnableIdempotence = false`, and an idempotent producer always waits for every in-sync replica. A producer configured with `Acks.None` or `Acks.Leader` and idempotence left on now stops the host from starting with an explanation that names `DisableIdempotentProducer()`, rather than failing every send (`Acks.None`) or being silently raised to `Acks.All` (`Acks.Leader`). See [the transport defaults below](#sqs-inline-heartbeat-kafka-idempotent-producers-and-rabbitmq-prefetch-drain-are-on-in-7-0).
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
| `AmazonSqsQueue.ExtendVisibilityWhileHandling` (SQS `ProcessInline()` listeners) | `false`: the visibility timeout is set once on receive, and the later messages of a batch that outlives it are redelivered while still being handled | `true`: the visibility of every unsettled message in the batch is extended every half `VisibilityTimeout`. Needs the `sqs:ChangeMessageVisibility` IAM permission | `.DoNotExtendVisibilityWhileHandling()` on the listener | `inline_visibility_heartbeat_4019` |
| `KafkaProducerOptions.EnableIdempotence` | off: Confluent.Kafka producers were not idempotent unless `UseIdempotentProducer()` | on: Dekaf's idempotent producer (`acks=all`). `Acks.None` or `Acks.Leader` stops the host unless idempotence is off | `kafka.DisableIdempotentProducer()`, or `.DisableIdempotentProducer()` on a topic | `kafka_idempotent_producer_default_5201` |
| `RabbitMqQueue.DrainWaitForPrefetch` | `false`: a stopping listener cancels without waiting, and the broker redelivers its prefetched deliveries | `true`: the listener waits for its prefetched deliveries, bounded by `DrainTimeout`, before closing the channel | `.DoNotDrainWaitForPrefetch()` on the listener | `drain_wait_for_prefetch_turned_off` |
| `WithInMemoryIdempotency()` on `ProcessInParallelWithNativeAcks()` endpoints | off: no guard unless the listener asks for one | on, with the default 5 minute window and 100,000 id ceiling. Every other endpoint mode still opts in | `.WithoutInMemoryIdempotency()` on the listener | `core_default_flips_5200` |
| `DurabilitySettings.MessageIdentity` | `IdOnly`: an inbox row is identified by its envelope id | `IdAndDestination`: by its envelope id and the endpoint that received it. **This changes the primary key of the inbox table (and the dead letter table) in the database**; see [below](#messageidentity-defaults-to-idanddestination-in-7-0-schema-change) | `opts.Durability.MessageIdentity = MessageIdentity.IdOnly` | `restore_v6_defaults_tests` |
| `EnableDeterministicQueueOrdering` (PostgreSQL, SQL Server, MySQL, Oracle and SQLite transports) | off: new queue tables are dequeued in timestamp order alone | on for **new** queue tables: a `queue_order` identity column and indexes (an index over the rowid on SQLite) break timestamp ties. Existing tables keep their layout; see [queue tables](#new-queue-tables-get-deterministic-ordering-and-sql-server-s-throughput-layout) | `.DisableDeterministicQueueOrdering()` on the persistence registration | `queue_tables_new_only` |
| `SqlServerTransport.OptimizeQueueThroughput` | off: new SQL Server queue tables get the classic layout | on for **new** queue tables: the throughput layout, a clustered `seq` identity. Existing tables keep their layout | `.UseClassicQueueLayout()` on the persistence registration (explicit, so it also rebuilds existing throughput tables) | `queue_tables_new_only` |

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
  everything deriving from it, which since JasperFx 3.0 includes `JasperFx.Events.StreamLockedException`, plus
  `JasperFx.Events.ExistingStreamIdCollisionException` from a `StartStream` whose id is already taken) on a
  transactional endpoint now answer 409 with ProblemDetails instead of escaping as a 500, and the OpenAPI document
  advertises the 409. If you call `MapConcurrencyFailuresToConflict()`, `MapMartenConcurrencyFailuresToConflict()` or
  `MapPolecatConcurrencyFailuresToConflict()` yourself, your call replaces the default, so a custom filter or
  ProblemDetails factory still applies. Marten 10, Polecat 6 and Fisher 2 all throw the shared JasperFx.Events types,
  so the default covers every store with one mapping. See [exception handling](/guide/http/exception-handling).
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

### New queue tables get deterministic ordering and SQL Server's throughput layout

The database transports (PostgreSQL, SQL Server, MySQL, Oracle and SQLite) create **new** queue tables with
deterministic ordering in 7.0 (GH-5192). On PostgreSQL, MySQL, Oracle and SQL Server that is a `queue_order` identity
column plus composite indexes. On SQLite it is an index over the rowid, with no new column. On SQL Server, new tables
also get the [throughput layout](/guide/durability/sqlserver#optimizing-queue-throughput): a clustered `seq` identity
with a unique non-clustered index on the message id.

**Existing queue tables keep the layout they have.** No migration path adds the identity column or rebuilds an existing
table: not queue setup, not `resources setup` or `db-apply`, and not the schema check. Each listener reads the layout
of the table it actually finds, per database and per tenant database. So a tenant database created before the upgrade
and one created after it work side by side. The one exception is SQLite, where existing tables gain the two indexes on
the next migration, without a rebuild.

An explicit setting still behaves as it did in 6.x, so it **does** change existing tables:

* `EnableDeterministicQueueOrdering()` adds the identity column to existing tables. On PostgreSQL and MySQL that
  rewrites the table under an exclusive lock, and on SQL Server it numbers every row.
* `OptimizeQueueThroughput()` and `UseClassicQueueLayout()` on SQL Server rebuild an existing table that is on the
  other layout, under an exclusive lock. SQL Server's layout-switch rebuild (GH-4989) now only fires when you set the
  layout explicitly, which ends the rebuild ping-pong between two applications that disagree about the default.

To keep the 6.x layout for new tables too, call `opts.RestoreV6Defaults()` (GH-5511). It turns both defaults off on
every database transport, and like the 7.0 default it never touches an existing table: one that already has the column
or the throughput layout keeps it. An explicit `EnableDeterministicQueueOrdering()` or `OptimizeQueueThroughput()` still
wins over `RestoreV6Defaults()`, in either order.

You can also turn them off one at a time: call `DisableDeterministicQueueOrdering()` on the persistence registration
(`PersistMessagesWithPostgresql(...)`, `PersistMessagesWithSqlServer(...)` and so on). Tables that already have the
column keep it. On SQL Server, `UseClassicQueueLayout()` also keeps new tables on the classic layout. Because that one
is explicit, it rebuilds any existing throughput-layout table back to classic, so drain the queues first.

To convert an existing table on purpose, follow "Converting an existing queue table" on the page for your database,
for example [SQL Server](/guide/durability/sqlserver#converting-an-existing-queue-table). Every application that
shares the queue tables has to be configured the same way.

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

### The leadership lock probe is asynchronous (BREAKING)

Each database store answers "does this node still hold the leadership lock?" by pinging the database session that
holds the lock, because PostgreSQL, SQL Server and MySQL drop a session-level lock the moment that session ends, and
the client can't tell until it uses the connection. In 6.x that ping was blocking I/O on the leadership health check.
7.0 makes it asynchronous (GH-5135):

- `INodeAgentPersistence.HasLeadershipLock()` is replaced by `Task<bool> HasLeadershipLockAsync(CancellationToken)`.
  Only code that implements `INodeAgentPersistence` itself, or stubs it in tests, has to change.
- `IMessageDatabase.AdvisoryLock` is now an `IWolverineAdvisoryLock`, which adds
  `HasLockAsync(int lockId, CancellationToken)` to JasperFx's `IAdvisoryLock`. The synchronous `HasLock` is still
  there, because `IAdvisoryLock` requires it.

The cancellation token only bounds the wait for the lock's own connection, and cancelling it never counts as losing
the lock. On PostgreSQL the ping still runs on the lock's dedicated connection and never inside a transaction, so the
session keeps reading as idle with no open transaction, as Marten's async daemon needs.

### SQS inline heartbeat, Kafka idempotent producers and RabbitMQ prefetch drain are on in 7.0

Three transport defaults that were opt-in through 6.x are on in 7.0 (GH-5201). `RestoreV6Defaults()` turns all three
off again, wherever in your configuration you call it, and an explicit setting wins either way.

**SQS inline listeners extend visibility while handling.** A `ProcessInline()` listener works through the up to ten
messages of a receive one at a time, so the last one ages against the visibility timeout through every handler before
it. In 6.x a batch that outlived the timeout had its later messages redelivered, and executed a second time, while
they were still being handled. 7.0 turns on `ExtendVisibilityWhileHandling()`: every half `VisibilityTimeout`,
Wolverine extends the visibility of every message of the batch that is still unsettled, for at most 12 hours. A batch
that finishes inside half the timeout costs nothing extra. One that doesn't makes `ChangeMessageVisibilityBatch` calls,
which SQS bills, and each one needs the **`sqs:ChangeMessageVisibility`** IAM permission. A
`ProcessInParallelWithNativeAcks()` listener already needed that permission in 6.x, because it always renews. An
inline listener needs it now too. Without it the calls fail, Wolverine logs a warning, and the messages are redelivered
at their visibility timeout as they were in 6.x. Buffered and durable listeners delete the message before the handler
runs and don't renew anything. To turn it off on one listener, call `.DoNotExtendVisibilityWhileHandling()`.

**Kafka producers are idempotent.** Wolverine 7's Kafka transport runs on Dekaf, whose producers are idempotent
(`enable.idempotence = true`, which implies `acks=all`), and Wolverine keeps that default. Java clients have defaulted to
it since Kafka 3.0. Idempotent writes need no extra broker permission on the Kafka 4.0+ brokers Wolverine 7 requires.
Brokers older than 2.8 also wanted the `IdempotentWrite` ACL. A producer that sets `Acks.None` or `Acks.Leader` with
idempotence on stops the host at startup, with a message that names the opt-out. Use
`opts.UseKafka(...).DisableIdempotentProducer()` for every producer, or `.DisableIdempotentProducer()` on one topic's
publishing configuration, which is also where a topic with its own `ConfigureProducer(...)` options has to opt out.
`RestoreV6Defaults()` turns idempotence off on every producer that doesn't set `EnableIdempotence` itself, so 6.x
`Acks.Leader` configurations keep working.

**RabbitMQ listeners wait for prefetched deliveries when they stop.** In 6.x a stopping, pausing or restarting listener
cancelled its consumer without waiting, and the broker redelivered everything still in the client's prefetch buffer,
counting each one against a Rabbit MQ 4 quorum queue's delivery limit. 7.0 turns on `DrainWaitForPrefetch()`: the
listener waits for the broker's cancel confirmation, so those deliveries reach the listener and, for a durable
listener, the inbox before the channel closes. The wait is bounded by `opts.Durability.DrainTimeout`, but it does slow
the drain. On Kubernetes, check that the pod's `terminationGracePeriodSeconds` (30 seconds by default) covers it. To
turn it off on one listener, call `.DoNotDrainWaitForPrefetch()`.

### One handler chain per handler type (BREAKING)

In 6.x, when several handler classes handled the same message type, Wolverine combined them by default
(`MultipleHandlerBehavior.ClassicCombineIntoOneLogicalHandler`) into one handler chain, run in one transaction. In 7.0
there is always **one handler chain per handler type**, which is what `MultipleHandlerBehavior.Separated` did in 6.x
(GH-5136, GH-5137, GH-5138). There is no way back to combined handlers, and `RestoreV6Defaults()` doesn't change this.
`WolverineOptions.MultipleHandlerBehavior` and the `MultipleHandlerBehavior` enum are removed (GH-5139), so delete the
line. In 6.52 and later, `opts.AssertNoCombinedHandlers()` lists the message types that 6.x still combines, so run it on
6.x first to see what will change. In 7.0 it is an `[Obsolete]` no-op, kept for one major version so the rehearsal still
compiles; remove the call after upgrading.

A message type with a single handler class is not affected by anything in this section.

**Each handler type runs on its own.** Every chain gets its own service scope, transaction, error policies and
deduplication scope. Handlers of the same message no longer commit together, and one failing no longer rolls back the
others.

**Sagas:**

* One saga type's `Start`, `Handle` and `NotFound` methods for a message are still one saga chain.
* A saga and a plain handler for the same message are two chains. In 6.x the plain handler could be silently dropped.
* Several saga types for one message no longer need any opt-in.

**New startup errors:**

* Two methods on one non-saga handler class for the same message type. Merge them into one method or move one to
  another class.
* Two handler types sticky to the same endpoint for one message type.

**Sticky handlers always apply.** 6.x only honoured `[StickyHandler]` (and the other ways of making a handler sticky)
when a message type had more than one handler. A lone sticky handler is now bound only to its endpoint as well.

**Local queues.** When a message type has more than one handler type, each non-sticky chain gets its own local queue
named after the handler type, `local://{handler type's full name, lower case}`. Publishing the message puts one copy
on each of those queues. A message arriving from a broker on an endpoint with no chain bound to it is relayed to every
handler type's local queue. If no chain can take a message at an endpoint, Wolverine throws
`NoHandlerForEndpointException`, naming where each handler type runs. A message type with one handler type keeps its
message-type queue, as before.

`LocalQueueFor<T>()` or `[LocalQueue]` on a message type with several handler types still creates that queue, but only
so it can drain rows persisted to it under 6.x, which it relays to every handler type's queue. Nothing routes to it any
more, and Wolverine logs a warning at startup saying so. **Settings you put on that queue (parallelism, durability,
ordering) no longer apply**, so move them to the handler types' queues, for example with
`opts.LocalQueue("{handler type's full name, lower case}")`.

With conventional broker routing, a listener for such a message type is now named for the handler type, as it was with
`Separated` in 6.x, so expect new broker queues or subscriptions.

**Message identity is unchanged.** `DurabilitySettings.MessageIdentity` still defaults to `MessageIdentity.IdOnly`, so
the inbox and dead letter tables keep their 6.x primary keys. If a broker can deliver one message to more than one of a
message type's durable listening endpoints in the same process, every copy has the same envelope id, and under `IdOnly`
the durable inbox rejects all but the first as a duplicate. Wolverine logs a warning at startup naming the message types
and endpoints where that can happen. Opt in to `opts.Durability.MessageIdentity = MessageIdentity.IdAndDestination` for
that setup, as in 6.x; it changes the primary key of the inbox table.

**Batching.** A `BatchMessagesOf<T>()` batch is now one more subscriber of `T` next to its handler chains, and its
queue name (`{queue}-batch`) is unchanged. `opts.AssertNoBatchHandlerConflicts()` and the 6.x startup warning about a
batch shadowed by a direct handler are removed, because both now run.

#### InvokeAsync and StreamAsync with several handler types

* **`InvokeAsync(message)`** runs every chain inline, one after another in discovery order, each with its own envelope,
  scope, transaction and error handling. The first failure stops the run and is rethrown as
  `Wolverine.Runtime.Handlers.HandlerInvocationException` (with `MessageType` and `HandlerType`). Its message lists the
  handler types that already completed and committed, and those that did not run. **The chains that ran before the
  failing one are not rolled back.**
* **`InvokeAsync<T>(message)`** and **`StreamAsync<T>(message)`** run only the one chain whose handler returns `T`
  (`Task<T>`, `ValueTask<T>`, a tuple containing `T`, or `IAsyncEnumerable<T>` for streaming). The other chains do not
  run. If no chain or more than one chain returns `T`, they throw `AmbiguousHandlerChainException`.
* The HTTP transport's invoke endpoint used to answer 400 for these message types. A request that asks for a reply now
  runs the one chain that produces it, and any other request runs every chain.

#### Generated code and handler graph APIs

A message type with one handler class keeps its generated type name. With several handler classes, the 6.x combined
chain's class (named for the message type) disappears and each chain gets a class named for its handler type, falling
back to `{Message}_{HandlerType}Handler` when two names collide. **If you pre-generate code (`codegen write`,
`TypeLoadMode.Static`), regenerate it** after upgrading.

For code that inspects the handler graph:

* `HandlerGraph.ChainFor(Type)` and `ChainFor<T>()` throw `AmbiguousHandlerChainException` when a message type has
  several chains. Use `ChainsFor(Type)` / `ChainsFor<T>()` for every chain of a message type, or
  `ChainFor(messageType, handlerType)` / `ChainFor<TMessage, THandler>()` for one of them.
* A chain's `Handlers` holds the calls of one handler type only. A test that asserted several handler classes on one
  chain should look each chain up with `ChainFor(messageType, handlerType)` instead.
* `MessageHandlerDescriptor` (the handler description CritterWatch reads from a service) has a new `HandlerType`
  property, the one handler type of the chain.
* `HandlerGraph.Chains` and `AllChains()` are one flat list of every chain. There are no more per-endpoint child chains,
  and `HandlerChain.ByEndpoint` is gone.

### Store integrations share one implementation in 7.0

Wolverine.Marten, Wolverine.Polecat and Wolverine.Fisher (and their HTTP packages) now run on one shared
implementation in Wolverine's core for their outbox, event forwarding, aggregate handling, saga diagnostics and
`[DocumentExists]` code (GH-5141 to GH-5145). The public types you use keep their names and namespaces. What
changed is what they do:

* **The observer hears about appended events only after the commit.** Polecat and Fisher used to report appended events
  to `IWolverineObserver.EventsAppended` (which CritterWatch listens to) before the commit, so a failed commit was still
  reported. All three stores now report after the commit, as Marten already did.
* **The saga explorer works on Polecat and Fisher.** It used to read relational saga tables those stores never write
  to, so it showed nothing.
* **Polecat refuses `Solo` or `HotCold` daemon modes combined with Wolverine-managed event subscription distribution**
  at startup instead of hanging, as Marten already did.
* **Projection side effects on an ancillary Polecat or Fisher store** enlist in that store's own message store, not the
  main one.
* **Scoped subscription runners** (`SubscribeToEventsWithServices`) set the tenant, and Marten's keeps its service scope
  until the after-commit flush.
* **A string-keyed ancillary store next to a Guid-keyed main store** now builds its chains, and Fisher unwraps nullable
  aggregate and saga ids as Polecat already did.
* **`[DocumentExists]` / `[DocumentDoesNotExist]`** check the chain's own store, so a chain routed to an ancillary Marten
  store no longer asks the main `IDocumentStore`.
* **One 409 mapping for every store** (GH-5459). `MapConcurrencyFailuresToConflict()` maps `JasperFx.ConcurrencyException`
  (including `JasperFx.Events.StreamLockedException`) and `JasperFx.Events.ExistingStreamIdCollisionException` to 409,
  which covers Marten's, Polecat's and Fisher's exceptions alike. The `MartenStreamLockedMiddleware` and
  `PolecatStreamLockedMiddleware` classes are **removed**; they were no longer registered by anything. Remove any
  `AddMiddleware(typeof(MartenStreamLockedMiddleware))` of your own, since the core mapping already catches that type
  (see the [Wolverine.Http defaults](#wolverine-http-defaults-that-changed-in-7-0)).
* New store-agnostic side effects on `Storage`: `Storage.DeleteById<T>(id)`, `Storage.DeleteWhere<T>(filter)`,
  `Storage.ArchiveStream(id)` and `Storage.TombstoneStream(id)`.

### `Saga.LastModified` is set by every saga store

The base `Wolverine.Saga` type has a new `public DateTimeOffset LastModified { get; set; }` next to `Version`
(GH-5345). Wolverine sets it to `DateTimeOffset.UtcNow` every time it inserts or updates a saga, in the generated
saga code, so it means the same thing on every store and is the property to order by, or filter on, when you look
for saga instances that have sat untouched for a long time. A value your own code assigns is overwritten on the
next save.

What each store needs from you:

| Store | Schema change | Sagas saved before 7.0 |
|---|---|---|
| Marten | none; the property is also mapped to Marten's own `mt_last_modified` column | loaded with their `mt_last_modified` value |
| Lightweight saga tables (PostgreSQL, SQL Server, MySQL, SQLite, Oracle) | none; every table already had a `last_modified` column. It is now written with the property's value | PostgreSQL, SQL Server, MySQL and SQLite load the column into the property. Oracle reads `default(DateTimeOffset)` until the saga's next update |
| EF Core | **yes**: the base-class property becomes a mapped column in your `DbContext`. Add an EF Core migration for it, or `modelBuilder.Entity<MySaga>().Ignore(x => x.LastModified)` to keep the old schema (the property is still set in memory) | the column's value from your migration's default (or `default(DateTimeOffset)`) until the saga's next update |
| Polecat, Fisher, RavenDb, CosmosDb, Redis, Azure Blob Storage, Amazon S3, in-memory | none; the value is saved with the rest of the saga | `default(DateTimeOffset)` until the saga's next update |

On EF Core, a saga that is loaded and saved again is now always written: setting `LastModified` makes the entity
`Modified`, which also increments `Version`. Before 7.0, a message that changed nothing on the saga left the row and
its version alone.

If a saga type of yours already declares its own `LastModified`, it now hides the base property (compiler warning
CS0108). Wolverine still sets the base `Saga.LastModified`; rename or remove your own property.

### Obsolete in 7.0

Wolverine.Marten, Wolverine.Polecat and Wolverine.Fisher each shipped their own copy of a few public types. 7.0 adds one
core type for each and marks the per-store copies `[Obsolete]` (GH-5514). The old types still work exactly as before
and are removed in 8.0.

| Obsolete in 7.0 | Use instead |
|---|---|
| `Wolverine.Marten.Requirements.DocumentExistsAttribute<T>`, `Wolverine.Polecat.Requirements.DocumentExistsAttribute<T>`, `Wolverine.Fisher.Requirements.DocumentExistsAttribute<T>` | `Wolverine.Persistence.Requirements.DocumentExistsAttribute<T>` |
| `Wolverine.Marten.Requirements.DocumentDoesNotExistAttribute<T>`, and the Polecat and Fisher copies | `Wolverine.Persistence.Requirements.DocumentDoesNotExistAttribute<T>` |
| `Wolverine.Marten.IEventForwarding`, `Wolverine.Polecat.IEventForwarding`, `Wolverine.Fisher.IEventForwarding` | `Wolverine.Persistence.EventSourcing.Forwarding.IEventForwarding` |
| `Wolverine.Marten.EventForwardingTransform<T>`, and the Polecat and Fisher copies | `Wolverine.Persistence.EventSourcing.Forwarding.EventForwardingTransform<T>` |

* **The core attributes keep the old names.** To migrate, swap `using Wolverine.Marten.Requirements;` (or the Polecat or Fisher
  one) for `using Wolverine.Persistence.Requirements;`. They work with all three stores. A chain routed to an ancillary
  store with `[Storage]` or `[MartenStore]` checks that store. If more than one document store is integrated, route
  the chain with `[Storage(typeof(...))]`: otherwise the attribute refuses at code generation rather than guess. Do not import both namespaces into one file, or `[DocumentExists<T>]` is ambiguous.
  That means a file that also uses `IMartenDataRequirement` (from `Wolverine.Marten.Requirements`) should spell the
  attribute's full name.
* **Event forwarding.** `MartenIntegration`, `PolecatIntegration` and `FisherIntegration` implement the core
  `IEventForwarding`. Their public `SubscribeToEvent<T>()` keeps its signature until 8.0, and what it returns is already a
  core `EventForwardingTransform<T>`. Code that never names these types (`x.SubscribeToEvent<T>().TransformedTo(...)`)
  needs no change. Code that names the interface should alias it,
  `using IEventForwarding = Wolverine.Persistence.EventSourcing.Forwarding.IEventForwarding;`, because `Wolverine.Marten`
  still carries the obsolete name.
* The `DocumentStore()` extension methods (`Wolverine.Polecat.TestingExtensions`, `Wolverine.Fisher.TestingExtensions`
  and Marten's own) are **not** obsolete and stay as they are.
* The `MartenOps`, `PolecatOps` and `FisherOps` facades are not obsolete in 7.0.

### MQTT transport runs on MQTTnet 5; `WolverineFx.Mqtt5` retired (BREAKING)

In 6.x the MQTT transport came in two packages that shared one `Wolverine.MQTT` namespace: `WolverineFx.MQTT` on MQTTnet 4
(through `MQTTnet.Extensions.ManagedClient`, whose last release is 4.3.7) and `WolverineFx.Mqtt5` on MQTTnet 5. In 7.0 there is
one package:

- **`WolverineFx.MQTT` is built on MQTTnet 5** (and `MQTTnet.Server` for `LocalMqttBroker`).
- **`WolverineFx.Mqtt5` is retired.** It is not published for 7.0; its last versions are the 6.x releases
  (6.64.0 when this was written). If you referenced it, reference `WolverineFx.MQTT` instead. The namespace, the types and
  `UseMqtt(...)` are the same, so that is the whole migration.

`UseMqtt(...)`, `UseMqttWithLocalBroker(...)`, the topic configuration and the `IManagedMqttClient` /
`ManagedMqttClientOptionsBuilder` types keep their shape. MQTTnet 5 has no managed client, so Wolverine ships its own in the
same `MQTTnet.Extensions.ManagedClient` namespace. It reconnects after `AutoReconnectDelay` (5 seconds by default) when the
connection drops, re-subscribes to its topics on reconnect, and queues outgoing messages while disconnected, up to
`MaxPendingMessages`. MQTT keep-alive is MQTTnet 5's own (`WithKeepAlivePeriod(...)` on the client options).

Wolverine's managed client is smaller than MQTTnet 4's:

- `ManagedMqttClientOptionsBuilder` has `WithClientOptions(...)`, `WithAutoReconnectDelay(...)` and
  `WithMaxPendingMessages(...)`. MQTTnet 4's other builder methods (`WithStorage(...)`,
  `WithPendingMessagesOverflowStrategy(...)` and the rest) are gone; remove the calls.
- `WithClientOptions(...)` replaces the whole client options each time it is called, so configure the client in one call.
- When `MaxPendingMessages` messages are already queued, enqueuing another throws `InvalidOperationException`. MQTTnet 4
  dropped the new message by default.

If you moved from `WolverineFx.MQTT` 6.x, code that touches MQTTnet types directly needs MQTTnet 5's names. In
particular:

| MQTTnet 4 | MQTTnet 5 |
|---|---|
| `using MQTTnet.Client;` (client options, builders, event args) | `using MQTTnet;` |
| `new MqttFactory()` | `new MqttClientFactory()` (clients), `new MqttServerFactory()` (servers) |
| `MqttApplicationMessage.PayloadSegment` (read) in a custom `IMqttEnvelopeMapper` | `Payload` (a `ReadOnlySequence<byte>`; `Payload.ToArray()` with `using System.Buffers;`) |
| `new MqttClientTcpOptions { Server = "host", Port = 1883 }` | `new MqttClientTcpOptions { RemoteEndpoint = new DnsEndPoint("host", 1883) }`, or `WithTcpServer("host", 1883)` on the builder |
| `MqttExtendedAuthenticationExchangeData`, `SendExtendedAuthenticationExchangeDataAsync` | `MqttEnhancedAuthenticationExchangeData`, `SendEnhancedAuthenticationExchangeDataAsync` |
| `MQTTnet.Diagnostics` (`IMqttNetLogger`) | `MQTTnet.Diagnostics.Logger` |

The wire format is unchanged: a 6.x node on either package and a 7.0 node exchange messages through the same broker.

### Dependency major versions in 7.0 (BREAKING)

Wolverine 7 is part of the Critter Stack .NET 11 release, and its packages depend on the new majors of the rest of the
stack. Upgrade them together; mixing a Wolverine 7 package with Marten 9 or Weasel 9 does not work.

| Package | Wolverine 6.x | Wolverine 7.0 |
|---|---|---|
| JasperFx, JasperFx.Events | 2.x | 3.0 |
| JasperFx.RuntimeCompiler (used by `WolverineFx.RuntimeCompilation`) | 5.x | 6.0 |
| Weasel | 9.x | 10.0 |
| Marten | 9.x | 10.0 |
| Polecat | 5.x | 6.0 |
| Fisher | 1.x | 2.0 |
| Microsoft.Extensions.*, EF Core, ASP.NET Core OpenAPI | 9.x / 10.x | 10.x on net10.0, 11.x on net11.0 |
| Npgsql | 9.x | 10.x |
| Microsoft.Data.SqlClient | 6.x | 7.x |
| Swashbuckle.AspNetCore (Wolverine.Http samples and tests) | 6.x | 10.x on net10.0, 11.x on net11.0 |
| OpenTelemetry | 1.15 | 1.20 |
| Kafka client | Confluent.Kafka | Dekaf (see [above](#kafka-transport-now-runs-on-dekaf-breaking)) |
| MQTT client (`WolverineFx.MQTT`) | MQTTnet 4.3 + MQTTnet.Extensions.ManagedClient | MQTTnet 5 (see [below](#mqtt-transport-runs-on-mqttnet-5-wolverinefx-mqtt5-retired-breaking)) |
| NATS client (`WolverineFx.Nats`, `WolverineFx.ClaimCheck.Nats`) | NATS.Net 2.x | NATS.Net 3.x (see [below](#nats-transport-runs-on-nats-net-3)) |

The Marten, Polecat and Fisher majors have their own migration guides; read them alongside this one. The RabbitMQ,
Azure Service Bus, SQS/SNS, GCP Pub/Sub, Pulsar, Redis and Cosmos DB client libraries are unchanged.

#### NATS transport runs on NATS.Net 3

`WolverineFx.Nats` and `WolverineFx.ClaimCheck.Nats` move from NATS.Net 2.x to 3.x. No Wolverine NATS API changed
shape: the NATS.Net types Wolverine's public surface takes or exposes keep their names and namespaces in 3.x, and now
come from the 3.x assemblies. They are `NatsOpts` (`ConfigureNatsOpts`), `NatsAuthCred` (`AuthCallback`),
`NatsJSPubOpts` (`ConfigureJetStreamPublish`), `StreamConfigRetention` / `StreamConfigStorage` /
`StreamConfigDiscard` / `StreamSource` / `Placement` (`StreamConfiguration`), `ConsumerConfigDeliverPolicy` (`DeliverFrom`),
`NatsConnection` / `INatsJSContext` (`NatsTransport.Connection` / `JetStreamContext`),
`NatsMsg<byte[]>` / `INatsJSMsg<byte[]>` (`NatsEnvelope`), and `INatsConnection` / `INatsObjContext`
(`NatsObjectStoreClaimCheckStore`). Code that uses them compiles unchanged, but an application that also references
NATS.Net directly must move to 3.x with Wolverine. Behaviour that differs, from the NATS.Net 3 upgrade notes:

- A core NATS subscription's pending channel holds 16,384 messages before it drops the newest, up from 1,024. Set
  `SubPendingChannelCapacity` through `ConfigureNatsOpts()` to keep the old bound.
- NATS.Net's own request-reply defaults to `NatsRequestReplyMode.Direct`. Wolverine's request-reply does not use it;
  your own `RequestAsync` calls on Wolverine's connection do.
- `NatsOpts.SkipSubjectValidation` is `[Obsolete]`.

### Schema changes when the first 7.0 node starts

Several 7.0 defaults add schema objects to an existing message database. With the default
`AutoBuildMessageStorageOnStartup = AutoCreate.CreateOrUpdate`, **the first 7.0 node to start runs that DDL** against
the main message database and every tenant database. Everything it adds is additive:

* the `wolverine_deduplication_hashed` and `wolverine_deduplicated_responses` tables
  ([deduplication storage](#deduplication-storage-is-on-by-default-enablemessagededuplication-removed-breaking))
* the inbox/outbox `timestamp` columns, the outbox stale index and `wolverine_nodes.load_factor`
  ([optional schema objects](#optional-message-storage-schema-objects-are-always-created-in-7-0))
* the ordering indexes on existing SQLite queue tables
  ([queue tables](#new-queue-tables-get-deterministic-ordering-and-sql-server-s-throughput-layout))

Before the first 7.0 node starts:

* **Every application sharing the message storage schema must be on 6.50 or later.** Wolverine only stopped dropping
  unknown columns from its envelope and queue tables in 6.50, so an older node could drop the new columns again.
* **If you run with `AutoCreate.None`**, apply the migration yourself (`dotnet run -- resources setup`, `db-apply`, or
  your own migration process) against every message database, including each tenant database.
* **On PostgreSQL**, adding the outgoing `timestamp` column rewrites the outbox table under an exclusive lock. Migrate
  while a large outbox is drained.

`RestoreV6Defaults()` turns off the deduplication tables and the optional schema objects, so it avoids all of this
except the SQLite queue indexes.

### OpenTelemetry meter and metric names (BREAKING)

Wolverine's metrics move to the OpenTelemetry naming conventions in 7.0 (GH-5128):

* **The `Meter` is named exactly `Wolverine`**, the same as Wolverine's `ActivitySource`. It used to be
  `Wolverine:{ServiceName}`, so `AddMeter("Wolverine")` matched nothing and the Aspire dashboard listed one meter
  per application. `AddMeter("Wolverine")` now collects every Wolverine metric; `AddMeter("Wolverine*")` still works
  and also collects the separate `Wolverine.AI` and `Wolverine.CritterWatch` meters. A `MeterListener` or a view that
  compared the meter name to `"Wolverine:" + serviceName` has to compare it to `"Wolverine"`
  (`MetricsConstants.MeterName`). Several Wolverine hosts in one process now share one meter name, so tell them
  apart by the `source` tag, or by the `IWolverineRuntime.Meter` instance in a test.
* **The service is identified by the resource, not the meter name.** The OpenTelemetry SDK takes the resource's
  `service.name` from `OTEL_SERVICE_NAME`, and every Wolverine measurement still carries the `source` tag with
  `WolverineOptions.ServiceName`.
* **`WolverineOptions.ServiceName` defaults from `OTEL_SERVICE_NAME`** when it is set (an Aspire AppHost sets it for
  every project resource), and otherwise from the entry assembly as before. An explicit `opts.ServiceName`, or a
  `JasperFxOptions.ServiceName` set through `CritterStackDefaults()`, still wins.
  The name is also what node records, CritterWatch and the Event Model use, so if a service ran with
  `OTEL_SERVICE_NAME` set to something other than its assembly name, set `opts.ServiceName` explicitly to keep the
  old name.
* **Instrument names are dotted.** Backends that keep OpenTelemetry names as they are (OTLP into the Aspire
  dashboard, Honeycomb, Datadog and so on) see the new names, so rename those series in dashboards, alerts and
  queries. Only the separators changed, so a Prometheus exporter, which turns both `-` and `.` into `_`, produces the
  same `wolverine_messages_sent` style names as before. Units are unchanged.
* **The message type tag is `wolverine.message.type`** (GH-5450), where it was the bare `message.type`
  (`MetricsConstants.MessageTypeKey`). `message.type` is an old OpenTelemetry RPC semantic-convention attribute, so
  backends that migrate old semantic-convention names on ingest (Dash0 by default) renamed Wolverine's tag to
  `rpc_message_type`, and anything grouping by message type found nothing. Unlike the instrument names, this one
  changes under a Prometheus exporter too: `message_type` becomes `wolverine_message_type`, so update every
  `by (message_type, ...)` grouping and `{message_type="..."}` selector in dashboards, alerts and queries. The other
  tag keys (`message.destination`, `tenant.id`, `source`, `schedule.name`, `exception.type`) are unchanged, and so
  is the tracing tag `messaging.message_type`. There is no `RestoreV6Defaults()` switch for this, the same as for
  the instrument names.

| 6.x instrument | 7.0 instrument |
|---|---|
| `wolverine-messages-sent` | `wolverine.messages.sent` |
| `wolverine-execution-time` | `wolverine.execution.time` |
| `wolverine-messages-succeeded` | `wolverine.messages.succeeded` |
| `wolverine-dead-letter-queue` | `wolverine.dead_letter_queue` |
| `wolverine-fault-publish-failures` | `wolverine.fault_publish.failures` |
| `wolverine-effective-time` | `wolverine.effective.time` |
| `wolverine-inbox-count` | `wolverine.inbox.count` |
| `wolverine-outbox-count` | `wolverine.outbox.count` |
| `wolverine-scheduled-count` | `wolverine.scheduled.count` |
| `wolverine-database-connection-count` | `wolverine.database.connection.count` |
| `wolverine-database-connection-budget` | `wolverine.database.connection.budget` |
| `wolverine-leases-renewed` | `wolverine.leases.renewed` |
| `wolverine-leases-lost` | `wolverine.leases.lost` |
| `wolverine-lease-ceiling-reached` | `wolverine.lease.ceiling_reached` |
| `wolverine-duplicates-suppressed` | `wolverine.duplicates.suppressed` |
| `wolverine-idempotency-early-rotation` | `wolverine.idempotency.early_rotation` |
| `wolverine-messages-received` | `wolverine.messages.received` |
| `wolverine-execution-failure` | `wolverine.execution.failure` |
| `wolverine-recurring-occurrences-skipped` | `wolverine.recurring.occurrences_skipped` |

| 6.x tag key | 7.0 tag key | Prometheus label (6.x → 7.0) |
|---|---|---|
| `message.type` | `wolverine.message.type` | `message_type` → `wolverine_message_type` |

### Changed during 6.x without a flag

These changed in 6.x minor releases with no setting to put the old behaviour back in place, so `RestoreV6Defaults()`
doesn't touch them. If you are coming from an early 6.x release, check each one:

| Change | Shipped in | What to do |
|---|---|---|
| **Kafka `CommitMode` default** became `StoreThenAutoFlush` (GH-3150): offsets are stored per finished message and committed by the client's background committer, where earlier releases committed synchronously after every message | 6.13 | Nothing, unless you need strict per-message commits: then call `.CommitOffsets(CommitMode.PerMessage)`. See [Kafka behaviour changes](#kafka-behaviour-changes) for how commits run on Dekaf |
| **GCP Pub/Sub competing consumers share one subscription** (GH-4615), named after the topic, where each node used to create its own `{subscription}.{node number}` and handle every message | 6.41 | Let the old nodes drain their per-node subscriptions, then delete them by hand; Wolverine doesn't. Expect duplicates during a rolling deploy unless the listener is durable. `.SubscriptionPerNode()` on a listener keeps the old behaviour. See [GCP Pub/Sub listening](/guide/messaging/transports/gcp-pubsub/listening) |
| **NATS named consumers are created or updated on startup**: an existing JetStream consumer is brought in line with the listener's ack, `AckWait`, `MaxDeliver`, filter subject and `MaxAckPending` settings, where it used to be reused as it was | 6.47 | If you manage consumers outside the application, call `.Provisioning(NatsProvisioning.CreateOnly)` on `UseNats(...)`. See [NATS](/guide/messaging/transports/nats) |
| **SQL Server-ordered envelope ids** (GH-4902): when every message store is SQL Server (including the SQL Server transport, EF Core on SQL Server and Polecat) and `EnvelopeIdGeneration` isn't set, Wolverine switches to `NewIdSqlServerOrder` at startup | 6.49 | Nothing; no schema change. Set `EnvelopeIdGeneration` yourself, or pass `optimizeEnvelopeIdsForSqlServer: false`, to opt out. See [`GuidV7`](#guidv7-is-the-default-envelope-id-in-7-0) |
| **Cosmos DB inbox documents are partitioned by message identity** (GH-5059) under `MessageIdentity.IdOnly`, where they were partitioned by destination, so the same id arriving at two addresses is now recognized as a duplicate | 6.51 | Nothing; the container and partition key path are unchanged, and documents written by older versions still drain. Until they have, a redelivery of one of them isn't rejected. See [Cosmos DB](/guide/durability/cosmosdb) |
| **Omitted HTTP values bind the declared default** (GH-5099): an omitted query string, header or form value binds the parameter's default (`int limit = 50` binds 50, not 0) | 6.51.2 | Nothing; see [Wolverine.Http defaults](#wolverine-http-defaults-that-changed-in-7-0) |
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
