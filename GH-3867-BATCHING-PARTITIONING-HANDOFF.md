# GH-3867: `BatchMessagesOf()` composing with partitioned sequential processing

**Written 2026-08-07.** Issue: [wolverine#3867](https://github.com/JasperFx/wolverine/issues/3867).
Driver: [CritterWatch#949](https://github.com/JasperFx/CritterWatch/issues/949).

**Branch: `cw949-group-id-batching`** (off `main` @ `1131eac79`, the 6.24.10 tag commit).
**Both parts are now implemented.** This note records why, what was deliberate, and what is still
not covered.

---

## TL;DR

A batched handler could not participate in `PartitionProcessingByGroupId` sequential ordering. Two
independent causes:

1. **The batch envelope carried no group id** — and a null group id means *a random slot*, not "no
   partitioning". **Fixed** (`GroupByGroupId()`).
2. **The batch envelope was never routed** — `BatchingProcessor` hardcoded its destination, so the
   batch always executed on one dedicated local queue no matter what partitioning was configured.
   **Fixed** (shape 3, below).

Consequence worth stating plainly, because it surprised two separate reviewers and was true until
part 2: **`GlobalPartitioned` did not give you a single writer per group id if any participating
message type was batched.**

---

## Part 1 — `BatchingOptions.GroupByGroupId()`

`BatchingOptions.GroupByGroupId()` opts into `GroupIdMessageBatcher<T>`
(`src/Wolverine/Runtime/Batching/GroupIdMessageBatcher.cs`), which groups by `(TenantId, GroupId)`
and stamps the group id onto each produced batch envelope.

Why it is needed, precisely — `PartitionedMessagingExtensions.SlotForProcessing:51`:

```csharp
var groupId = rules.DetermineGroupId(envelope);
if (groupId == null) return Random.Shared.Next(1, numberOfSlots) - 1;   // <-- random, not "unpartitioned"
```

`DefaultMessageBatcher<T>` groups only by `TenantId`, so a batch spans group ids and has none of its
own; `DetermineGroupId` then falls through to the rules, which cannot find an identity on a `T[]`.
So a batched handler on a sharded endpoint silently drew a different slot every trigger. No
configuration error, nothing in the logs.

Design points that were deliberate, please preserve them:

- **Key is `(tenant, group)`, never group alone.** Members settle against the batch envelope
  (`Envelope(object message, IEnumerable<Envelope> batch)` sets `InBatch` on each), so merging
  tenants would lose the tenant each member arrived under. This narrows `DefaultMessageBatcher`'s
  behaviour rather than replacing it.
- **Ungroupable envelopes batch together and stay ungrouped.** The batcher does not invent an
  identity.
- **Rules are injected, not resolved.** `MessagePartitioningRules` is not available when
  `BatchingOptions` is configured, so `ProcessorBuilder.Build` sets it through the internal
  `IRequirePartitioningRules`. `DetermineGroupId` prefers an already-set `Envelope.GroupId` and
  writes the resolved value back, so on a listener already using `PartitionProcessingByGroupId` the
  id is present before the batching processor ever sees it.

Tests: `src/Testing/CoreTests/Acceptance/group_id_message_batcher.cs`, 5 tests.

---

## Part 2 — shape 3, as preferred

> **The requirement:** a batched message must execute on the same local, partitioned queue that an
> unbatched message of the same group id would execute on.

For a message type participating in a partitioned topology, the batch envelope for group `G` goes to
slot `hash(G) % N` — `global-{base}{slot}` under global partitioning, `{base}{slot}` under
`PublishToPartitionedLocalMessaging`. No partitioned topology covering the type → the previous
single-queue behaviour, unchanged.

### How it is wired

- **`WolverineRuntime.resolveBatchExecutionTopologies()`** runs at bootstrap, right after
  `applyBatchProbePolicies()` and well before the transports start. For each `BatchDefinition` whose
  element type matches a `GlobalPartitioned` (companion local queues) or
  `PublishToPartitionedLocalMessaging` topology, it records the slot endpoints on
  `BatchingOptions.ExecutionSlots`.
- **`IBatchExecutionQueues`** (`src/Wolverine/Runtime/Batching/BatchExecutionQueues.cs`) selects the
  queue per assembled batch, so `ProcessorBuilder.Build`'s single `ILocalQueue` resolve becomes N.
  `PartitionedBatchExecutionQueues` uses **`SlotForSending`** — the same hash
  `GlobalPartitionedRoute` and `PartitionedMessageTopology.SelectSlot` use, so the batch agrees with
  where the unbatched messages for that group went. (`SlotForSending` and `SlotForProcessing`
  deliberately use *different* hashes; the topology layer is the former. Getting this wrong would
  silently reintroduce the race, so there is a test pinning it.)
- **The batcher is auto-swapped** to `GroupIdMessageBatcher<T>` when it is still the built-in
  default, since slotting requires a batch to belong to exactly one group. An application-supplied
  batcher is left alone, and any batch it emits without a group id falls back to the dedicated queue
  rather than drawing a random slot.
- **Opting out:** `ExecuteOnDedicatedLocalQueue()`, or setting `LocalExecutionQueueName` — naming a
  queue is read as meaning it. Wolverine's own default assignment goes through the internal
  `SetDefaultLocalExecutionQueueName` so it does not read as a user choice.

Automatic rather than opt-in because `GlobalPartitionLocalQueueUri` is `internal`: users cannot wire
this themselves, and an application that declared `GlobalPartitioned` for a message type has already
stated the intent that the batch was silently exempting itself from.

### On the deadlock question

The earlier note here was right that there is **no direct self-deadlock**: `HandleAsync` runs on the
slot block but only posts into `_batchingBlock`, while `processEnvelopes` runs on a separate
`_processingBlock`, so the enqueue happens off the slot block.

The head-of-line hazard flagged alongside it is real, though, and it is worse than a stall — it
closes a cycle across three bounded buffers:

```
slot block (bounded, DOP 1) → BatchingProcessor.HandleAsync
  → BatchingChannel._inner (bounded) → addItem → _processingBlock (bounded)
  → processEnvelopes → queue.EnqueueAsync → back into the same slot block
```

Saturate all three and every worker in the ring is blocked on the next. This did not exist before,
because the batch's dedicated local queue is unbounded (GH-3287).

The fix is `Endpoint.HostsBatchExecution`, set on every slot endpoint a batch targets;
`DurableReceiver` gives those an unbounded execution block. `EnqueueAsync` into an unbounded slot
never blocks, so `_processingBlock` never stalls and the cycle cannot close — which also removes the
cross-group head-of-line coupling, without the drop risk of a non-blocking `Post`. Back-pressure is
not lost: `BatchingPendingCounts` counts members against the originating external listener, which is
what `ListeningAgent.QueueCount` watches. `BufferedReceiver` already passes unbounded for local
queues, so only the durable path needed the change.

### The two other checks that were asked for, and their answers

- **Does the batch chain resolve on the companion queues?** Yes. `ExecutorFor(T[], slot)` falls
  through to the default chain; and for the Separated-mode case with sticky `Handle(T[])` handlers,
  `HandlerGraph.HandlerFor` already special-cases a local queue with `UsedInShardedTopology` and
  builds a fanout. Covered end to end by the acceptance test below.
- **Does `BatchingPendingCounts.SettleBatch` still fire once per batch?** Yes, by construction. Both
  `DurableReceiver.CompleteAsync` and `BufferedReceiver`'s channel callback settle on
  `envelope.Batch != null`, keyed off the batch envelope itself and independent of which queue it
  landed on. Each grouped envelope still goes to exactly **one** queue — this is slot selection, not
  fan-out — so there is no double-count and no lost settle.

### Also fixed

`BatchReplay.EnqueueReducedBatchAsync` copied `Destination`, `MessageType` and `TenantId` but **not
`GroupId`**, so a `ProbeIndividuallyAfter` or `ApplyItemException` probe lost the batch's identity
and scattered the survivors across slots. That was already wrong with part 1 alone.

---

## What is still NOT covered

Shape 3 only reaches configurations where the unbatched handlers execute on an *addressable local
queue*:

| Configuration | Unbatched handlers run on | Covered? |
|---|---|---|
| `GlobalPartitioned` | companion local queue `global-{base}{slot}` | **yes** |
| `PublishToPartitionedLocalMessaging` | local queue `{base}{slot}` | **yes** |
| Plain listener + `PartitionProcessingByGroupId` | the listener receiver's own `ShardedExecutionBlock` | **no** |

The third row is not a queue and cannot be enqueued to. The most a batched handler gets there is
part 1 plus group-sharding the batching queue, which sequences the batches against each other but
not against the unbatched handlers. The answer today is to move to one of the two topologies;
closing it properly would mean making a listener's sharded block addressable, which is a much larger
change.

One more edge, noted rather than solved: under `MultipleHandlerBehavior.Separated` with **multiple**
sticky `Handle(T[])` handlers, the batch fans out from the slot queue to each sticky handler's own
queue, so those handlers execute off-slot and the sequencing guarantee does not extend to them.

---

## Reproduction and verification

**The pure-Wolverine acceptance test now exists and needs no broker:**
`src/Testing/CoreTests/Acceptance/batching_with_partitioned_processing.cs`. `ExplicitRouting` already
sends a batched element type to its topology slots, so a `PublishToPartitionedLocalMessaging`
topology reproduces the whole thing in memory. One batched and one unbatched message type share a
group id; the test asserts no intra-group overlap while still observing cross-group parallelism (so
a fix that merely serialized everything would not pass).

**It fails on `main`** — 12 violations of exactly the shape described above.

```bash
dotnet test src/Testing/CoreTests/CoreTests.csproj -f net9.0 \
  --filter "FullyQualifiedName~batching_with_partitioned_processing"
```

Also `batch_execution_topology_resolution.cs` (bootstrap resolution and the opt-outs),
`BatchExecutionQueuesTests` (slot selection, including agreement with `SlotForSending`), and
`BatchReplayTests` (group id survives a probe).

Full CoreTests on net9.0: 2,315 passed / 0 failed. `dotnet build wolverine.slnx -c Release -f net9.0`
clean.

**Not yet run:** the broker-backed `global_partitioned_sharded_processing` suites (RabbitMQ, Kafka,
SQS, Postgres, SqlServer), which need `docker compose up -d`.

The downstream effect also reproduces in CritterWatch's soak harness (`src/IngestSoakTests` in
`~/code/CritterWatch`, target `./build.sh IngestSoak`), where a batched `ServiceUpdates` handler and
~10 unbatched handlers all append to one Marten event stream:

| run | conflicts |
|---|---|
| 4 hosts / 1 service / 60 min, unbatched writers active | **4,275** `EventStreamUnexpectedMaxEventIdException` (71/min), across 7 handler types, batched one = 59% |
| same batched volume, unbatched writers off | **0** |

`CW_SOAK_DISCOVERY_CHURN=0` is the negative control. That harness is the field acceptance test: with
this in, the churn-on run should go to zero conflicts while keeping cross-service parallelism
(compare the `many_services_many_nodes` topology before and after). **It has not been re-run.**

The field data point that decided the shape: the affected CritterWatch console wires
`GlobalPartitioned` *unconditionally* — one `AddCritterWatchServices` call supplying
`configureClusterShardedTopology` with 5 sharded slots across RabbitMQ, SQS and Azure Service Bus.
So the failing deployment already had the strongest partitioning Wolverine offers and still saw ~20
stream-concurrency exceptions/min, because global partitioning sequenced every participating message
type *except* the batched one.

---

## Hazards for whoever touches this next

- `MessagePartitioningRules.DetermineGroupId` is `internal` and **mutates the envelope** (writes the
  resolved id back). Fine inside the assembly; do not expose it casually.
- `GlobalPartitionLocalQueueUri` is `internal` — an outside assembly cannot bridge a listener to a
  chosen local queue.
- The `else if` at `ListeningAgent.cs:412` intercepts matching messages on *non-paired* endpoints
  when global topologies exist. Still untraced.
- CritterWatch carries its own `ServiceUpdatesBatcher` (uncommitted, in `src/CritterWatch.Services/`)
  that duplicates part 1. It should be deleted — with this branch, `ServiceUpdates` needs no batcher
  configuration at all, since the topology match does both the grouping and the slotting.
