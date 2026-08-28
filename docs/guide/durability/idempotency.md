# Idempotent Message Delivery

::: tip
There is nothing you need to do to opt into idempotent, no more than once message deduplication other than to be using the durable inbox
on any Wolverine listening endpoint where you want this behavior. 
:::

When applying the [durable inbox](/guide/durability/#using-the-inbox-for-incoming-messages) to [message listeners](/guide/messaging/listeners), you also get a no more than once, 
[idempotent](https://en.wikipedia.org/wiki/Idempotence) message delivery guarantee. This means that Wolverine will discard
any received message that it can detect has been previously handled. Wolverine does this with its durable inbox storage to check on receipt of a 
new message if that message is already known by its Wolverine identifier. 

Instead of immediately deleting message storage for a successfully completed message, Wolverine merely marks that the message is handled and keeps
that message in storage for a default of 5 minutes to protect against duplicate incoming messages. To override that setting, you have this option:

<!-- snippet: sample_configuring_keepaftermessagehandling -->
<a id='snippet-sample_configuring_keepaftermessagehandling'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        // The default is 5 minutes, but if you want to keep
        // messages around longer (or shorter) in case of duplicates,
        // this is how you do it
        opts.Durability.KeepAfterMessageHandling = 10.Minutes();
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/PersistenceTests/Samples/DocumentationSamples.cs#L188-L198' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_configuring_keepaftermessagehandling' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Logical Message Deduplication <Badge type="tip" text="6.31" />

::: warning
This is opt-in, and turning it on provisions a new `wolverine_deduplication` table. Leaving it off
means no schema change at all on upgrade.
:::

Everything above keys on `Envelope.Id`, which identifies **one delivery**. That is the right identity
for "the broker handed me this same message twice", and it is the wrong identity for a different
question:

> The operator clicked *Rebuild* twice. The console republished the command after a timeout. A
> scheduling agent pre-published tonight's 03:00 occurrence yesterday, and the scheduler published it
> again on the night. Should the projection rebuild four times?

Each of those is a *different* delivery of the *same intent*, so each carries a different
`Envelope.Id` and every one of them gets through. What is needed is an identity for the intent, and
that is what `DeduplicationId` is.

`Envelope.DeduplicationId` is a `string`, it is set by your application, and it already round-trips on
every transport Wolverine supports — it is written to and read from the `deduplication-id` wire header
by Wolverine's own serialization, so it survives Rabbit MQ, Kafka, Azure Service Bus, SQS, and the
rest without any transport-specific configuration. (Two transports also consume it natively: SQS/SNS
FIFO queues and topics, and GCP Pub/Sub.)

### Turning it on

<!-- snippet: sample_enabling_message_deduplication -->
<!-- endSnippet -->

### Deduplicating a message handler

<!-- snippet: sample_deduplicated_message_handler -->
<!-- endSnippet -->

and on the publishing side:

<!-- snippet: sample_publishing_with_a_deduplication_id -->
<!-- endSnippet -->

A logical id is a `string` rather than a `Guid` on purpose: it is meant to be legible in the database
when someone is working out why a job did not fire.

The second message with that id never reaches your handler. It is discarded, acknowledged to the
broker, and logged at `Information` — a duplicate that vanished without a trace would be
indistinguishable from a message that was lost.

### Where the id comes from

By default a message handler reads `Envelope.DeduplicationId`. You can point at a member of the
message itself instead, so publishers do not have to set `DeliveryOptions`:

<!-- snippet: sample_deduplicated_from_the_message_body -->
<!-- endSnippet -->

`ValueSource.Header` reads an envelope header, and `ValueSource.Anything` uses the chain type's
natural default.

### Required or optional?

`Required` defaults to `true`, and a message that arrives at a `[Deduplicated]` handler with no
logical id is dead-lettered with a `MissingDeduplicationIdException`.

That default is deliberately the strict one. The lenient reading is the dangerous one: a handler that
asked for deduplication and then quietly processed every unkeyed message would report itself as
protected while providing nothing, and the failure is invisible — the traffic succeeds, the
duplicates run, and no log line distinguishes it from a working configuration.

Set `Required = false` when a mixed stream is genuinely expected:

<!-- snippet: sample_deduplication_with_optional_id -->
<!-- endSnippet -->

Unkeyed messages on such a handler pay no database round trip at all.

### Applying it across many handlers

<!-- snippet: sample_requiring_deduplication_ids_by_policy -->
<!-- endSnippet -->

The filter is required rather than optional. Applying logical deduplication to *every* handler would
demand an id on traffic that has no business carrying one, and with `Required` defaulting to `true`
that turns into a dead-lettered message per unkeyed send.

### The deduplication window

`DeduplicationWindow` defaults to 24 hours, and it is the whole guarantee: past that window the same
logical id is accepted again.

It is deliberately **not** `KeepAfterMessageHandling`. That setting exists to absorb a broker
redelivery and defaults to five minutes, which would turn "idempotent" into "idempotent for a while" —
worse than no guarantee, because it holds in testing and fails in production. Size the window against
how long a duplicate could plausibly arrive: an operator double-click is seconds, a console republish
is minutes, an agent pre-publishing tomorrow's occurrences is a day.

A background reaper deletes expired claims on its own timer
(`Durability.DeduplicationCleanupPollingTime`, default 5 minutes), in bounded batches, in its own
transaction. It logs how many claims it removed each cycle — a count that keeps climbing is the signal
that the window is too long, the cadence too slow, or the volume higher than the settings assume.

### Failed handlers do not poison the id

If your handler throws, the claim is released and a retry gets through. Where the handler is
transactional the claim was written inside that transaction and the rollback removes it; where it is
not, Wolverine issues a compensating release.

This matters more than it sounds. Without it, the first failed attempt would permanently claim that
logical id, every retry would be discarded as a duplicate of its own failed attempt, and the work
would silently never happen while the logs reported successful deduplication.

### Why a separate table

The claims live in `wolverine_deduplication` rather than as a column on
`wolverine_incoming_envelopes`, for three reasons:

1. **Partitioning.** With `EnableInboxPartitioning` the inbox is `PARTITION BY LIST (status)`, and
   PostgreSQL will not create a unique index on a partitioned table unless the index carries the
   partition key. Marking an envelope handled updates `status`, which moves the row between
   partitions — so a `(deduplication_id, status)` index would let the same logical id exist once as
   `Incoming` and once as `Handled`, silently, and only for users who enabled partitioning.
2. **Retention.** A deduplication marker has to outlive the inbox row it came from, and the inbox is
   reaped on a five-minute default.
3. **Migration cost.** `wolverine_incoming_envelopes` is small, hot, and written by every running
   node. Adding a column is a migration; changing one later needs an `ACCESS EXCLUSIVE` lock that
   queues behind in-flight inbox writes. Its own table means the inbox schema never moves for this
   feature at all.

### Scope and limitations

- **Supported on:** message handlers and [Wolverine.HTTP endpoints](/guide/http/deduplication).
  Wolverine's gRPC services are not supported yet, and a `[Deduplicated]` gRPC method fails at
  bootstrap with a clear message rather than silently doing nothing.
- **Storage:** the RDBMS message stores — PostgreSQL, SQL Server, MySQL and SQLite (and therefore
  Marten-backed applications). Other stores report themselves as unsupported and fail loudly rather
  than passing every duplicate through.
- **Scope:** logical ids are unique per message store, not per tenant. In a database-per-tenant
  setup each tenant database has its own table and is therefore naturally isolated; under conjoined
  (single-database) tenancy the id is global.
- **Fire-and-forget only.** Replaying the *original response* of a deduplicated `InvokeAsync<T>` is
  Stripe-style idempotency-key machinery — storing and returning the prior result — and is not part
  of this feature. HTTP endpoints get a useful answer regardless, because a status code is a
  response; see below.
