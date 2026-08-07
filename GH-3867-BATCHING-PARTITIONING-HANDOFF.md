# Handoff — GH-3867: `BatchMessagesOf()` cannot compose with partitioned sequential processing

**Written 2026-08-07.** Issue: [wolverine#3867](https://github.com/JasperFx/wolverine/issues/3867).
Driver: [CritterWatch#949](https://github.com/JasperFx/CritterWatch/issues/949).

**Branch: `cw949-group-id-batching`** (off `main` @ `1131eac79`, the 6.24.10 tag commit).
One commit: `8bc8ded8a` — **part 1 only**. Part 2 is unstarted and is the real gap.

---

## TL;DR

A batched handler cannot participate in `PartitionProcessingByGroupId` sequential ordering. Two
independent causes:

1. **The batch envelope carries no group id** — and a null group id means *a random slot*, not "no
   partitioning". **Fixed on this branch.**
2. **The batch envelope is never routed** — `BatchingProcessor` hardcodes its destination, so the
   batch always executes on one dedicated local queue no matter what partitioning is configured.
   **Not fixed. This is the work.**

Consequence worth stating plainly, because it surprised two separate reviewers:
**`GlobalPartitioned` does not give you a single writer per group id if any participating message
type is batched.**

---

## Part 1 — done, on the branch

`BatchingOptions.GroupByGroupId()` (`src/Wolverine/Runtime/Batching/BatchingOptions.cs:81`) opts into
`GroupIdMessageBatcher<T>` (`src/Wolverine/Runtime/Batching/GroupIdMessageBatcher.cs`), which groups
by `(TenantId, GroupId)` and stamps the group id onto each produced batch envelope.

Why it is needed, precisely — `PartitionedMessagingExtensions.SlotForProcessing:51`:

```csharp
var groupId = rules.DetermineGroupId(envelope);
if (groupId == null) return Random.Shared.Next(1, numberOfSlots) - 1;   // <-- random, not "unpartitioned"
```

`DefaultMessageBatcher<T>` groups only by `TenantId`, so a batch spans group ids and has none of its
own; `DetermineGroupId` then falls through to the rules, which cannot find an identity on a `T[]`.
So today a batched handler on a sharded endpoint silently draws a different slot every trigger. No
configuration error, nothing in the logs.

Design points that were deliberate, please preserve them:

- **Key is `(tenant, group)`, never group alone.** Members settle against the batch envelope
  (`Envelope(object message, IEnumerable<Envelope> batch)` sets `InBatch` on each), so merging
  tenants would lose the tenant each member arrived under. This narrows `DefaultMessageBatcher`'s
  behaviour rather than replacing it.
- **Ungroupable envelopes batch together and stay ungrouped.** The batcher does not invent an
  identity. That leaves them exactly where they are today rather than making them worse.
- **Rules are injected, not resolved.** `MessagePartitioningRules` is not available when
  `BatchingOptions` is configured, so `ProcessorBuilder.Build` sets it through the internal
  `IRequirePartitioningRules` (`BatchingOptions.cs:158`). `DetermineGroupId` prefers an already-set
  `Envelope.GroupId` and writes the resolved value back, so on a listener already using
  `PartitionProcessingByGroupId` the id is present before the batching processor ever sees it.

Tests: `src/Testing/CoreTests/Acceptance/group_id_message_batcher.cs`, 5 tests, green on net9.0 and
net10.0.

```bash
dotnet test src/Testing/CoreTests/CoreTests.csproj --filter "FullyQualifiedName~GroupIdMessageBatcherTests"
```

**Not yet done for part 1:** no docs page update (`docs/guide/messaging/partitioning.md` and the
batching guide both describe the composition as if it works), and no sample. Worth adding once part
2 settles, since the two together are what a user needs.

---

## Part 2 — the actual gap

`BatchingProcessor.processEnvelopes` (`src/Wolverine/Runtime/Batching/BatchingProcessor.cs:62`):

```csharp
foreach (var grouped in Batcher.Group(envelopes))
{
    grouped.Destination = Queue.Uri;      // <-- the single LocalExecutionQueueName
    grouped.MessageType = Chain!.TypeName;
    grouped.SentAt = DateTimeOffset.UtcNow;
    await Queue.EnqueueAsync(grouped);
}
```

`Queue` comes from `BatchingOptions.LocalExecutionQueueName`, resolved once in
`ProcessorBuilder.Build`. The batch never goes through routing, so nothing downstream can act on the
group id part 1 now stamps.

Meanwhile the **unbatched** handlers for the same entity execute somewhere else entirely:

- On a plain external listener: on the listener's receiver. When `PartitionProcessingByGroupId` is
  applied, `BufferedReceiver.cs:63-69` / `DurableReceiver.cs:126-132` build a
  `ShardedExecutionBlock`, whose slots are `new Block<Envelope>(1, …)`
  (`ShardedExecutionBlock.cs:26`) — strictly ordered per slot.
- Under `GlobalPartitioned`: on a companion local queue, via `GlobalPartitionedReceiverBridge`
  installed at `ListeningAgent.cs:404-411` when `Endpoint.GlobalPartitionLocalQueueUri` is set.

Either way that is a **different execution block from the batching queue**. Batched and unbatched
handlers for the same group id run concurrently.

Useful thing already verified so you do not have to: **`LocalQueueConfiguration` extends
`ListenerConfiguration<…>`** (`src/Wolverine/Transports/Local/LocalQueueConfiguration.cs:6`), so
`PartitionProcessingByGroupId` *is* available on a local queue, and `BufferedLocalQueue` /
`DurableLocalQueue` both go through the receivers that honour `GroupShardingSlotNumber`. So
group-sharding the batching queue itself works today — it just does not help on its own, because the
other writers are on a different queue.

### Three shapes, no strong preference from me

1. **Honour a `Destination` the batcher set** instead of unconditionally overwriting it. Smallest
   change; pushes slot selection into batcher implementations, which then need topology knowledge.
2. **Route the grouped envelope** through the normal routing path so it flows into a partitioned
   local topology like any other message. Most consistent, but `MessageType`/`Destination`/`SentAt`
   are currently set by hand here for a reason — check what routing would and would not reproduce
   (notably `BatchingPendingCounts.SettleBatch`, and the CritterWatch#942 back-pressure accounting
   at `BatchingProcessor.cs:48`, which counts members against the originating listener).
3. **Let `BatchingOptions` take a partitioned local topology** rather than a single
   `LocalExecutionQueueName`, and pick the slot from the batch's group id. Most explicit; largest
   API surface.

The only hard constraint: a batch that has a group id must be able to land on that group's slot.

### Things to check before choosing

- `ProcessorBuilder.Build` resolves exactly one `ILocalQueue` up front
  (`BatchingOptions.cs:163`). Shapes 2 and 3 need N, or need resolution deferred per batch.
- Retry/dead-letter: `ProbeIndividuallyAfter` re-runs members as size-1 batches. Confirm a re-probed
  batch keeps its group id, or a poison probe will scatter across slots.
- `BatchingPendingCounts` settles once per grouped batch envelope. Confirm per-slot fan-out does not
  double-count or lose a settle.

---

## Reproduction and verification

The downstream effect reproduces reliably in CritterWatch's soak harness (`src/IngestSoakTests` in
`~/code/CritterWatch`, target `./build.sh IngestSoak`), where a batched `ServiceUpdates` handler and
~10 unbatched handlers all append to one Marten event stream:

| run | conflicts |
|---|---|
| 4 hosts / 1 service / 60 min, unbatched writers active | **4,275** `EventStreamUnexpectedMaxEventIdException` (71/min), across 7 handler types, batched one = 59% |
| same batched volume, unbatched writers off | **0** |

`CW_SOAK_DISCOVERY_CHURN=0` is the negative control. That harness is the acceptance test for part 2:
with the fix in, the churn-on run should go to zero conflicts while keeping cross-service
parallelism (compare the `many_services_many_nodes` topology before and after).

A pure-Wolverine equivalent would be better for this repo — a batched handler and an unbatched
handler for the same group id, both writing to a shared in-memory structure through a partitioned
endpoint, asserting no interleaving. That test does not exist yet and would be worth writing first,
since it fails today and is the tightest statement of the bug.

---

## Hazards

- `MessagePartitioningRules.DetermineGroupId` is `internal` and **mutates the envelope** (writes the
  resolved id back). Fine inside the assembly; do not expose it casually.
- `GlobalPartitionLocalQueueUri` is `internal` — an outside assembly cannot bridge a listener to a
  chosen local queue. Anything that expects users to wire this themselves is a non-starter.
- The `else if` at `ListeningAgent.cs:412` intercepts matching messages on *non-paired* endpoints
  when global topologies exist. I did not trace that path; if part 2 touches routing, read it.
- CritterWatch currently carries its own `ServiceUpdatesBatcher` (uncommitted, in
  `src/CritterWatch.Services/`) that duplicates part 1. It should be deleted in favour of
  `GroupByGroupId()` once this ships — do not treat it as a second implementation to keep in sync.
