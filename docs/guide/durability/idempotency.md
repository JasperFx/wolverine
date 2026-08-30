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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/PersistenceTests/Samples/DocumentationSamples.cs#L204-L214' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_configuring_keepaftermessagehandling' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Logical Message Deduplication <Badge type="tip" text="6.31" />

::: warning
This is opt-in, and turning it on provisions a new `wolverine_deduplication` table. Leaving it off
means no schema change at all on upgrade.
:::

Everything above keys on `Envelope.Id`, which identifies **one delivery**. That's helpful to make sure that Wolverine 
doesn't handle the exact same message more than once, but that's only keying off the Wolverine assigned message identity
and doesn't really help you when what you're concerned about is some logical message being accidentally sent to Wolverine
multiple times. Using the new *logical message deduplication* is allowing you to specify business logic concerns as
the identity for another level of protection.

For example:

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

::: info
We probably should have added this feature ages ago, but we did so in 6.31 to get ready for Wolverine to have a first
class chron message scheduler and use the deduplication id as a way of preventing multiple executions in a chaotic world.
:::

### Turning it on

<!-- snippet: sample_enabling_message_deduplication -->
<a id='snippet-sample_enabling_message_deduplication'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.PersistMessagesWithPostgresql("connection string");

        // Opt in to logical message deduplication. This provisions a new
        // "wolverine_deduplication" table -- nothing else about your message
        // storage changes, and leaving this off means no schema migration at all
        opts.Durability.EnableMessageDeduplication = true;

        // How long a logical id is honoured before the reaper removes it.
        // The default is 24 hours. This IS the guarantee, so size it against
        // how long a duplicate could plausibly arrive
        opts.Durability.DeduplicationWindow = 24.Hours();
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/PersistenceTests/Samples/DeduplicationSamples.cs#L37-L55' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_enabling_message_deduplication' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

::: warning
If a handler asks for deduplication and `EnableMessageDeduplication` is left off, Wolverine logs a
warning at startup and that handler **throws on its first message**. It is deliberately a warning
rather than a startup failure: `[Deduplicated]` lives on a handler type, and handler types are
discovered by every host that scans their assembly. In a modular monolith where one module wants
deduplication and a sibling host does not, a hard failure would stop the sibling from starting
through no fault of its own configuration.

What is *not* softened is the guarantee itself. A store that cannot enforce deduplication throws
rather than answering "yes, that's new" to every id, so a misconfigured host can never quietly
process a duplicate.
:::

### Deduplicating a message handler

<!-- snippet: sample_deduplicated_message_handler -->
<a id='snippet-sample_deduplicated_message_handler'></a>
```cs
public static class RebuildProjectionHandler
{
    // Wolverine will refuse to run this handler twice for the same
    // Envelope.DeduplicationId within the deduplication window
    [Deduplicated]
    public static void Handle(RebuildProjection command)
    {
        // rebuild the projection...
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/PersistenceTests/Samples/DeduplicationSamples.cs#L58-L71' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_deduplicated_message_handler' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

and on the publishing side:

<!-- snippet: sample_publishing_with_a_deduplication_id -->
<a id='snippet-sample_publishing_with_a_deduplication_id'></a>
```cs
public static ValueTask ScheduleNightlyRebuild(IMessageBus bus, string projectionName, DateTimeOffset occurrence)
{
    return bus.PublishAsync(new RebuildProjection(projectionName, occurrence), new DeliveryOptions
    {
        // The logical identity of the WORK, not of this particular delivery.
        // An operator double-click, a console republish, and an agent that
        // pre-published this occurrence yesterday all produce this same id
        DeduplicationId = $"{projectionName}|{occurrence:O}"
    });
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/PersistenceTests/Samples/DeduplicationSamples.cs#L73-L86' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_publishing_with_a_deduplication_id' title='Start of snippet'>anchor</a></sup>
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
<a id='snippet-sample_deduplicated_from_the_message_body'></a>
```cs
public static class CreateOrderHandler
{
    // Derive the logical id from a member of the message itself rather than
    // asking every publisher to set DeliveryOptions
    [Deduplicated(ValueSource.InputMember, nameof(CreateOrder.Sku))]
    public static void Handle(CreateOrder command)
    {
        // create the order...
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/PersistenceTests/Samples/DeduplicationSamples.cs#L88-L101' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_deduplicated_from_the_message_body' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

`ValueSource.Header` reads an envelope header, and `ValueSource.Anything` uses the chain type's
natural default.

### Deriving the id on the publishing side <Badge type="tip" text="6.31" />

Everything above is the *receiving* half. On the publishing side, asking every call site to remember
`DeliveryOptions.DeduplicationId` is exactly the kind of repetition that eventually gets forgotten at
one call site and silently un-protects a message. So a message type can declare its own logical
identity once, the same way it can already declare a topic name with `[Topic]` or a saga id with
`[SagaIdentity]`:

<!-- snippet: sample_deduplication_identity_on_a_member -->
<a id='snippet-sample_deduplication_identity_on_a_member'></a>
```cs
// The message type declares its own logical identity once, and every publisher
// gets it -- no DeliveryOptions at any call site
public record ArchiveInvoice([property: DeduplicationIdentity] string InvoiceNumber, DateOnly AsOf);
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/PersistenceTests/Samples/DeduplicationSamples.cs#L13-L19' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_deduplication_identity_on_a_member' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

or, for a contract whose members you cannot decorate, name the member from the type:

<!-- snippet: sample_deduplication_identity_naming_a_member -->
<a id='snippet-sample_deduplication_identity_naming_a_member'></a>
```cs
// The same thing for a contract whose members you cannot decorate
[DeduplicationIdentity(nameof(ReceiveShipment.ShipmentId))]
public record ReceiveShipment(Guid ShipmentId, string Warehouse);
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/PersistenceTests/Samples/DeduplicationSamples.cs#L21-L27' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_deduplication_identity_naming_a_member' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Either form is applied as an `IEnvelopeRule` when the message is routed, so it reaches every
transport, the local queues, and the outbox alike. Non-string members are converted with
`ToString()`.

When the identity is not a single member -- or the message type is generated and you cannot put an
attribute on it at all -- configure it instead:

<!-- snippet: sample_deriving_deduplication_ids -->
<a id='snippet-sample_deriving_deduplication_ids'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.PersistMessagesWithPostgresql("connection string");
        opts.Durability.EnableMessageDeduplication = true;

        // Compose the logical id from more than one member, or from anything
        // else you can reach from the message
        opts.MessageDeduplication.ByMessage<RebuildProjection>(
            x => $"{x.ProjectionName}|{x.OccurrenceUtc:O}");

        // Or, for generated message types you can neither decorate nor be
        // bothered writing a lambda for, use the first member that matches
        // one of these names
        opts.MessageDeduplication.ByMemberNamed("IdempotencyKey", "DeduplicationId");

        // Same thing as ByMessage<T>(), reached through the message type policies
        opts.Policies.ForMessagesOfType<CreateOrder>()
            .DeduplicateBy(x => $"{x.Sku}|{x.Quantity}");
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/PersistenceTests/Samples/DeduplicationSamples.cs#L105-L128' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_deriving_deduplication_ids' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

::: warning
Deriving an id does not deduplicate anything by itself. It only *stamps* `Envelope.DeduplicationId`.
Enforcement is still `[Deduplicated]` on the receiving handler or endpoint, plus
`Durability.EnableMessageDeduplication`. The two halves are deliberately separate: the publisher and
the consumer are frequently different applications, and the publisher should not have to know whether
anyone downstream is deduplicating.
:::

Precedence, when more than one of these could apply to the same message:

1. An explicit `DeliveryOptions.DeduplicationId` at the call site always wins.
2. Then anything registered on `opts.MessageDeduplication` (including
   `Policies.ForMessagesOfType<T>().DeduplicateBy()`), in registration order. Configuration beats the
   attribute on purpose -- an application should be able to override an identity baked into a
   contract it merely consumes.
3. Then `[DeduplicationIdentity]` on the message type.

No rule ever overwrites an id that is already set, and a rule that returns null or an empty string
leaves the message without one -- which is how you opt a particular message out.

### Required or optional?

`Required` defaults to `true`, and a message that arrives at a `[Deduplicated]` handler with no
logical id is dead-lettered with a `MissingDeduplicationIdException`.

That default is deliberately the strict one. The lenient reading is the dangerous one: a handler that
asked for deduplication and then quietly processed every unkeyed message would report itself as
protected while providing nothing, and the failure is invisible — the traffic succeeds, the
duplicates run, and no log line distinguishes it from a working configuration.

Set `Required = false` when a mixed stream is genuinely expected:

<!-- snippet: sample_deduplication_with_optional_id -->
<a id='snippet-sample_deduplication_with_optional_id'></a>
```cs
public static class MixedTrafficHandler
{
    // Some publishers set a logical id and some do not. Those that do are
    // protected; those that do not are handled exactly as if the feature
    // were off, and pay no database round trip
    [Deduplicated(Required = false)]
    public static void Handle(CreateOrder command)
    {
        // ...
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/PersistenceTests/Samples/DeduplicationSamples.cs#L151-L165' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_deduplication_with_optional_id' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Unkeyed messages on such a handler pay no database round trip at all.

### Applying it across many handlers

<!-- snippet: sample_requiring_deduplication_ids_by_policy -->
<a id='snippet-sample_requiring_deduplication_ids_by_policy'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        opts.PersistMessagesWithPostgresql("connection string");
        opts.Durability.EnableMessageDeduplication = true;

        // Apply logical deduplication to every handler matching a filter, instead
        // of decorating each one. Useful when the rule is "every create-style
        // command is deduplicated" and some of the handlers are not yours
        opts.Policies.RequireDeduplicationId(chain =>
            chain.MessageType.CanBeCastTo<ICreateCommand>());
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/PersistenceTests/Samples/DeduplicationSamples.cs#L133-L148' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_requiring_deduplication_ids_by_policy' title='Start of snippet'>anchor</a></sup>
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

::: info
This section does not apply to RavenDb or CosmosDb backed message persistence.
:::

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

- **Supported on:** message handlers, [Wolverine.HTTP endpoints](/guide/http/deduplication), and
  [Wolverine gRPC services](/guide/grpc/deduplication).
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
