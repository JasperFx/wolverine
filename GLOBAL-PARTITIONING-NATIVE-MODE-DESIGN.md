# Global partitioning "native mode" — design comparison

**GH-3481.** Deliverable for the design-first issue under the transport capability epic (GH-3482).
Nothing here is implemented yet; the point is to decide what is worth building.

> **DRAFT WORDING.** The analysis and the recommendation are the substance. The prose, the naming
> (`NativeOrdering`, "choreographed mode", …) and the badge/versioning choices are placeholders
> pending Jeremy's review.

---

## 1. What ships today ("choreographed mode")

`MessagePartitioning.GlobalPartitioned(...)` builds one uniform topology on all ten supported
transports (RabbitMQ, Kafka, Amazon SQS, Azure Service Bus, GCP Pub/Sub, NATS, Pulsar, Redis
Streams, PostgreSQL, SQL Server):

1. **N named endpoints**, `{base}1..{base}N`, created by a per-transport
   `PartitionedMessageTopology` subclass. The transport contributes *only* topology —
   `buildEndpoint` / `buildListener` / `buildSubscriber`.
2. **Wolverine computes the slot**: `Envelope.SlotForSending(N, rules)` — a hash of the envelope's
   `GroupId` modulo N. Slot counts are constrained to 3/5/7/9 (`PartitionSlots`).
3. **Wolverine chooses the owner**: each slot endpoint is `ListenerScope.Exclusive`, so exactly one
   node at a time holds the listening role, assigned by the agent-distribution machinery
   (`ExclusiveListenerFamily` → `AssignmentGrid.DistributeEvenly`).
4. **Every slot is forced to `EndpointMode.Durable`**, external *and* companion local.
5. **A companion local queue per slot** (`global-{base}{n}`), and the owning node short-circuits
   straight to it: `GlobalPartitionedRoute.CreateForSending` checks
   `FindListeningAgent(slotEndpoint.Uri).Status == Accepting` and, if this node owns the slot,
   never touches the broker at all.
6. **A re-route interceptor** (`GlobalPartitionedInterceptor`) catches globally-partitioned messages
   that arrive on some *other* listener and republishes them through the routing layer so they land
   on the right slot.

**The promise this makes to users:** no two messages sharing a `GroupId` are ever executed
concurrently anywhere in the cluster. It is a *per-slot* guarantee — one active consumer per slot,
and one `MaxDegreeOfParallelism`-bounded local queue behind it partitioned again by group id.

**What it costs:** N physical endpoints instead of one; Wolverine-side failover latency when a node
dies (agent reassignment, not a broker rebalance); and a hash that is only as even as the group-id
distribution.

---

## 2. What "native mode" would mean

Several brokers have a first-class primitive for exactly this problem. Native mode means handing the
partitioning *and the failover* to the broker instead of choreographing it.

| Transport | Native primitive | Ordering unit | Wolverine support today | Verdict |
|---|---|---|---|---|
| **Azure Service Bus** | Sessions (`SessionId`) | per **key** | Sessions already shipped (`RequireSessions()`, session-id pinning, session listeners reworked in GH-3494) | **Build** |
| **Kafka** | Topic partitions + consumer group | per **partition** (key → partition) | Sharded mode already shares one consumer group; `PropagateGroupIdToPartitionKey()` exists | **Build** |
| **Amazon SQS** | FIFO `MessageGroupId` | per **key** | `MessageGroupId` mapping + `EnableFairQueueMessageGroups()` shipped | **Document** |
| **GCP Pub/Sub** | Ordering keys | per **key** | `message.OrderingKey = envelope.GroupId` already wired; needs `EnableMessageOrdering` | **Document** |
| **Pulsar** | `KeyShared` subscription | per **key** | `SubscriptionType.KeyShared` already supported | **Document** |
| **NATS** | Subject mapping `{{partition(N,…)}}` + pinned pull consumers (2.11+) | per **partition** | Not wired; requires server-side subject mapping config | **Defer** |
| **RabbitMQ** | Super streams + single-active-consumer | per **partition** | Would need `RabbitMQ.Stream.Client` — a second client library and a second protocol | **Defer** |
| **Redis Streams** | — | — | Consumer groups give competing consumers, not ordering | **Non-goal** |
| **PostgreSQL / SQL Server** | — | — | The queue tables *are* the partitioning; there is nothing more native | **Non-goal** |

---

## 3. The questions the issue asks

### 3.1 Per-key vs per-slot ordering — what do we promise?

This is the central design tension and it is **not** a wash.

- **Choreographed mode is per-slot.** Two different group ids that hash to the same slot are
  serialized against each other. That is *stronger* than users asked for — it costs throughput but
  it is safe.
- **Native primitives are almost all per-key.** ASB sessions, SQS message groups, Pub/Sub ordering
  keys and Pulsar `KeyShared` all guarantee ordering *within a key* and allow unbounded parallelism
  *across* keys. That is exactly what users actually want, and it is strictly better — until the key
  cardinality explodes.
- **Kafka is the odd one out**: partition assignment is per-partition, so it is the same shape as
  choreographed mode, just with the broker doing the assignment.

**Recommendation:** do not try to unify these. Native mode should promise *at least as strong as*
per-key ordering and say so explicitly per transport, rather than pretending one sentence covers all
seven. The existing promise — "no two messages sharing a GroupId execute concurrently" — is honest
for both models; it is the *converse* (whether unrelated group ids can proceed in parallel) that
differs, and that is a performance property, not a correctness one.

**Caveat worth calling out in docs:** per-key modes have unbounded key cardinality. ASB sessions and
SQS message groups each carry broker-side state per active key, and a system that mints a fresh
group id per message will accumulate sessions/groups until it hits a service limit. Choreographed
mode has no such cliff — N is fixed. That alone is a reason to keep choreographed mode the default.

### 3.2 Poison-message head-of-line blocking

Materially worse under native mode, and this is the strongest argument against making it the default:

- **ASB sessions / SQS message groups:** a message that keeps failing blocks *its whole key* until
  it dead-letters. Under choreographed mode the same message blocks its slot, but the slot's local
  queue can keep draining other group ids up to `MaxDegreeOfParallelism` — a poison message degrades
  throughput rather than stopping a key outright.
- **Kafka partitions:** identical head-of-line problem, already familiar to Kafka users.
- **Pulsar `KeyShared`:** blocks the key; negative-ack redelivery is broker-native but
  [DotPulsar does not expose it](https://github.com/apache/pulsar-dotpulsar) (tracked on the epic's
  client-blocked watch list).

This is the same problem the **order-preserving DLQ epic (GH-3476)** exists to solve. Native mode
should be sequenced *after* that epic, not before it — shipping per-key ordering without an
order-preserving dead-letter path hands users a foot-gun.

### 3.3 The owned-listener local-queue shortcut

`GlobalPartitionedRoute` asks "does this node own slot K's listener?" and, when the answer is yes,
bypasses the broker entirely. That optimization **does not survive native mode**, because the node
genuinely does not know which keys it owns:

- ASB session ownership is decided at `AcceptNextSessionAsync` time and is not enumerable.
- Kafka partition assignment *is* knowable (the consumer's assignment set), but only after a
  rebalance settles, and it changes underneath you.
- Pub/Sub ordering keys are not assigned to subscribers at all in any observable way.

Three options:

1. **Drop the shortcut in native mode.** Every message goes to the broker. Simple, correct, and
   costs one broker round trip per message on the sending node. Given native mode's whole selling
   point is broker-managed assignment, paying the broker hop is philosophically consistent.
2. **Kafka-only shortcut** off the live consumer assignment set, invalidated on every rebalance.
   Real speedup, real complexity, and a rebalance race that would deliver out of order exactly once
   per rebalance. Not worth it.
3. **Keep companion local queues but feed them from the broker** rather than from the routing
   shortcut. This is just option 1 with the local queue still doing group-id-partitioned execution
   behind the listener — which is worth keeping, because it is what bounds concurrency per key on
   the receiving node.

**Recommendation: option 1 + option 3.** Native mode drops the send-side shortcut and keeps the
receive-side companion local queue.

### 3.4 New topology type, per-transport option, or documentation?

Three shapes were considered:

- **(a) A new topology type** — `MessagePartitioning.NativelyPartitioned(...)`. Clean separation,
  but duplicates the whole subscription/matching surface and forces users to learn a second concept.
- **(b) A per-transport option on the existing topology** —
  `topology.UseShardedAzureServiceBusQueues("orders", 4, t => t.UseNativeSessions())`. Keeps one
  concept, but the option quietly invalidates the `numberOfEndpoints` argument (native mode has no
  slots) and changes the ordering promise from under a shared configuration block. Misleading.
- **(c) Per-transport documented alternatives** — no new topology at all; instead, first-class docs
  showing the ASB-sessions / SQS-FIFO / Pub/Sub-ordering-keys / Pulsar-KeyShared configuration as
  the native alternative, with a decision table.

**Recommendation: (c) for the transports where the primitive already works today, plus a narrowly
scoped (a) for Kafka and ASB if demand appears.**

The reason is that for SQS, Pub/Sub and Pulsar, native mode is not a Wolverine feature at all — it
is *already* three lines of existing configuration. Wrapping those three lines in a topology
abstraction adds surface without adding capability. What is actually missing is the documentation
that tells users the choice exists and how to make it.

### 3.5 Which subset is worth building?

Ranked by (value × how much is missing):

1. **Documentation of the existing native alternatives** — SQS FIFO, Pub/Sub ordering keys, Pulsar
   `KeyShared`, ASB sessions. Zero code. Highest value per unit of effort by a wide margin. A
   decision table ("you want per-key ordering and you are only on ASB → use sessions; you want it
   portable → use global partitioning") belongs on the partitioning page.
2. **Kafka native partitions** — the one place where the current design is visibly *odd*: Wolverine
   creates N Kafka **topics** where Kafka already offers N **partitions** on one topic, with
   broker-managed assignment and (post-KIP-848) cheap rebalances. Real capability gap, and Wolverine
   already has `PropagateGroupIdToPartitionKey()`. Worth building.
3. **ASB native sessions as a topology** — only if users ask. The primitive ships; the gap is
   ergonomic, not functional.
4. **NATS partitioned subjects** — requires server-side subject-mapping configuration Wolverine
   cannot apply itself. Defer; revisit if NATS 2.11 pinned consumers become mainstream.
5. **RabbitMQ super streams** — a second client library, a second wire protocol, and no .NET
   framework precedent. Defer indefinitely; the epic's watch list is the right home.

---

## 4. Recommendation

**Do not build a general "native mode."** The uniform choreographed topology is the right default:
it is portable, it has no key-cardinality cliff, and its poison-message behavior is better than any
per-key primitive until GH-3476 lands.

Concretely, in order:

1. **(now, docs-only)** Add a "native alternatives" section to
   `docs/guide/messaging/partitioning.md`: a decision table plus a short per-transport recipe for
   ASB sessions, SQS FIFO message groups, Pub/Sub ordering keys and Pulsar `KeyShared`. Be explicit
   about the per-key/per-slot difference and the head-of-line-blocking trade.
2. **(after GH-3476)** Revisit per-key ordering as a first-class mode, now that an order-preserving
   dead-letter path exists.
3. **(independent)** Kafka native partitions as a distinct opt-in
   (`UseNativelyPartitionedKafkaTopic(...)` — one topic, N partitions, broker-assigned), which is a
   Kafka capability gap rather than a re-litigation of the partitioning model.

Everything else stays on the watch list.

---

## 5. Explicit non-goals

- **Redis Streams native mode.** Consumer groups distribute work; they do not order it. Nothing to
  back a native mode with.
- **PostgreSQL / SQL Server native mode.** The sharded queue tables *are* the primitive (GH-3468,
  GH-3469). There is no database-side ordering construct that would improve on them.
- **Unifying per-key and per-slot into one promise.** Attempting a single guarantee sentence that
  covers both would either overstate the per-key case or understate the per-slot one.

---

## 6. Related

- GH-3482 — transport capability gap analysis + global partitioning rollout (epic)
- GH-3476 — order-preserving DLQ epic (**blocks** any per-key native mode)
- GH-3467 — end-to-end global partitioning tests (surfaced that NATS global partitioning was broken
  outright and that Pulsar's companion queue names were malformed)
- GH-3468 / GH-3469 — PostgreSQL and SQL Server sharded topologies
- `GLOBAL-PARTITIONING-ROLLOUT-PLAN.md` — the original rollout plan
