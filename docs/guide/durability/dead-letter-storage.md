# Dead Letter Storage

If [message storage](/guide/durability/) is configured for your application, and you're using either the local queues or messaging
transports where Wolverine doesn't (yet) support native [dead letter queueing](https://en.wikipedia.org/wiki/Dead_letter_queue), Wolverine is actually moving messages
to the `wolverine_dead_letters` table in your database in lieu of native dead letter queueing. 

You can browse the messages in this table and see some of the exception details that led them to being moved
to the dead letter queue. To recover messages from the dead letter queue after possibly fixing a production support
issue, you can update this table's `replayable` column for any messages you want to recover with some kind of
SQL command like:

```sql
update wolverine_dead_letters set replayable = true where exception_type = 'InvalidAccountException';
```

When you do this, Wolverine's durability agent that manages the inbox and outbox processing in the background
will move these messages back into active incoming message handling. Just note that this process happens
through some polling, so it won't be instantaneous.

To replay dead lettered messages back to the incoming table, you also have a command line option:

```bash
dotnet run -- storage replay
```

## Indexing <Badge type="tip" text="6.16" />

The durability agent's background replay and cleanup cycles both filter the `wolverine_dead_letters`
table on the `replayable` column (and, when [dead letter expiration](#dead-letter-expiration) is
enabled, on the `expires` column). On a large dead-letter table these queries would otherwise force a
full table scan on every cycle. Wolverine automatically provisions indexes for these columns as part
of the normal schema creation/migration — a partial index scoped to the matching rows on PostgreSQL,
SQL Server, and SQLite, and a plain index on MySQL and Oracle (which don't support filtered indexes).
There is nothing to configure.

::: warning
If you are upgrading with an *already very large* `wolverine_dead_letters` table, be aware that the
schema migration that adds these indexes runs a normal `CREATE INDEX`, which takes a write-blocking
lock for the duration (potentially minutes on a multi-GB table). If that matters for your deployment,
create the indexes ahead of time with `CREATE INDEX CONCURRENTLY` (PostgreSQL) / `WITH (ONLINE = ON)`
(SQL Server) before rolling out the upgrade.
:::

## Introspecting an Endpoint's Dead Letter Destination <Badge type="tip" text="6.9" />

Where an endpoint's dead letters actually go varies by transport and configuration: some endpoints
move failures to Wolverine's durable `wolverine_dead_letters` storage, while others use a **native
broker dead letter queue** (RabbitMQ DLX, an SQS dead letter queue, the Azure Service Bus
`$DeadLetterQueue`, etc.) that a tool managing the durable store can't see.

Every endpoint declares its effective destination through a single transport-agnostic enum,
`DeadLetterStorageMode`, so monitoring tools can introspect it without transport-specific knowledge:

| Value | Meaning |
|-------|---------|
| `Durable` | Dead letters go to Wolverine's durable store (`wolverine_dead_letters`) — queryable and replayable through `IDeadLetters`. |
| `Native` | Dead letters go to a native broker dead letter queue and are **not** bridged into durable storage. |
| `NativeWithRecovery` | Dead letters go to a native broker dead letter queue **and** are bridged back into durable storage via [`EnableDeadLetterQueueRecovery()`](/guide/messaging/transports/rabbitmq/deadletterqueues.html#recovering-native-dead-letters-to-durable-storage). |

It is exposed two ways:

- `Endpoint.DeadLetterStorage` on the endpoint model.
- `EndpointDescriptor.DeadLetterStorage` on the diagnostic descriptor surface that monitoring tools
  (for example [CritterWatch](https://critterwatch.jasperfx.net)) read.

This lets a monitor detect endpoints that dead-letter **natively without recovery** (`Native`) and
recommend enabling recovery so those dead letters become visible and replayable in the durable store.

## Dead Letter Expiration <Badge type="tip" text="3.9" />

::: tip
You could see poor performance over time if the dead letter queue storage in the database gets excessively large,
so Wolverine does have an "opt in" feature to let old messages expire and be expunged from the storage.
:::

It's off by default (for backwards compatibility), but you can enable Wolverine to assign expiration times to dead letter
queue messages persisted to durable storage like this:

<!-- snippet: sample_enabling_dead_letter_queue_expiration -->
<a id='snippet-sample_enabling_dead_letter_queue_expiration'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {

        // This is required
        opts.Durability.DeadLetterQueueExpirationEnabled = true;

        // Default is 10 days. This is the retention period
        opts.Durability.DeadLetterQueueExpiration = 3.Days();

    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/BootstrappingSamples.cs#L40-L53' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_enabling_dead_letter_queue_expiration' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Note that Wolverine will use the message's `DeliverBy` value as the expiration if that exists, otherwise, Wolverine will
just add the `DeadLetterQueueExpiration` time to the current time. The actual stored messages are deleted by background
processes and won't be quite real time.

Archived dead letters can have their own retention period. See [Archiving and Restoring](#archiving-and-restoring).

## Failure Groups <Badge type="tip" text="6.54" />

A dead letter queue with thousands of messages in it is rarely thousands of different problems. More often it's a
handful of bugs, each of which failed a lot of messages. Wolverine "fingerprints" every message as it's moved to dead
letter storage so that you can work with the dead letters one *failure group* at a time.

By default, the failure group is a hash of the exception type and the first stack frame that comes from your
application code. That means:

* The same bug lands in one group no matter what data was in the exception message. The exception message is
  removed from the stack trace text before it's parsed, and line numbers are ignored so a redeployment doesn't split
  a group
* Two different bugs that happen to throw the same exception type land in different groups
* Stack frames from `System.*`, `Microsoft.*`, `Npgsql.*`, `MySqlConnector.*`, `Oracle.*`, `Wolverine.*`,
  `JasperFx.*`, `Weasel.*`, `Marten.*`, `Polecat.*`, `Fisher.*`, `Lamar.*` and Wolverine's generated handler code are
  skipped. If no frame qualifies, the first frame is used
* An exception without a stack trace (like a message recovered from a broker's native dead letter queue through
  `EnableDeadLetterQueueRecovery()`) falls back to the exception type alone, unless the message already carries a
  `failure-group` header stamped where the original exception was caught

Each failure group also gets a human readable title like `System.InvalidOperationException: OrderHandler.Handle(...)`.
The relational stores keep the group in the indexed `failure_group` column and the title in `failure_title`, and every
dead lettered envelope also carries both as the `failure-group` and `failure-title` headers
(`DeadLetterQueueConstants.FailureGroupHeader` and `FailureTitleHeader`).

You can add your own namespaces to the list of skipped "framework" frames:

<!-- snippet: sample_configuring_dead_letter_fingerprinting -->
<a id='snippet-sample_configuring_dead_letter_fingerprinting'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        // Treat your own shared infrastructure as framework code, so the failure group is
        // decided by the first frame from your business code instead
        var fingerprinter = (DefaultDeadLetterFingerprinter)opts.Durability.DeadLetterFingerprinter;
        fingerprinter.FrameworkNamespacePrefixes.Add("MyCompany.Messaging.");

        // Archived dead letters are deleted 30 days after they were archived
        opts.Durability.ArchivedDeadLetterRetention = 30.Days();
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/Persistence/dead_letter_failure_group_samples.cs#L14-L28' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_configuring_dead_letter_fingerprinting' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Or replace the fingerprinting altogether with your own `IDeadLetterFingerprinter`:

<!-- snippet: sample_custom_dead_letter_fingerprinter -->
<a id='snippet-sample_custom_dead_letter_fingerprinter'></a>
```cs
public class ByMessageTypeAndExceptionFingerprinter : IDeadLetterFingerprinter
{
    public DeadLetterFingerprint Fingerprint(Envelope envelope, Exception? exception)
    {
        var exceptionType = exception?.GetType().Name ?? "Unknown";
        var title = $"{exceptionType} handling {envelope.MessageType}";

        // Hashed() turns any string into a stable, compact group key
        return DeadLetterFingerprint.Hashed($"{envelope.MessageType}|{exceptionType}", title);
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/Persistence/dead_letter_failure_group_samples.cs#L87-L101' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_custom_dead_letter_fingerprinter' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Registering it in your application's container replaces the default. You can also assign it directly to
`opts.Durability.DeadLetterFingerprinter`:

<!-- snippet: sample_registering_a_custom_dead_letter_fingerprinter -->
<a id='snippet-sample_registering_a_custom_dead_letter_fingerprinter'></a>
```cs
using var host = await Host.CreateDefaultBuilder()
    .UseWolverine(opts =>
    {
        // Replaces the default fingerprinter
        opts.Services.AddSingleton<IDeadLetterFingerprinter, ByMessageTypeAndExceptionFingerprinter>();
    }).StartAsync();
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/Persistence/dead_letter_failure_group_samples.cs#L33-L42' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_registering_a_custom_dead_letter_fingerprinter' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

::: tip
Dead letters persisted before Wolverine had failure fingerprinting have no failure group. Summaries report them in one
group per exception type, keyed as `unfingerprinted:<exception type>` with a title saying they were recorded before
failure fingerprinting. That key works as a `FailureGroup` filter like any other. Nothing needs to be backfilled.
:::

## Managing Dead Letters Programmatically

Everything below is available through the `IDeadLetters` service of any message store, which you can get from
`IMessageStore.DeadLetters`. Most of it is driven by `DeadLetterEnvelopeQuery`, and every operation that takes a query
applies the same criteria:

| Property | Meaning |
|----------|---------|
| `FailureGroup` | Only dead letters in this failure group <Badge type="tip" text="6.54" /> |
| `ExceptionMessage` + `ExceptionMessageMatch` | Match the exception message with `DeadLetterMessageMatch.Exact` or `DeadLetterMessageMatch.Contains`. Leaving the match mode null keeps the original behavior, where the value is used as a SQL `LIKE` pattern <Badge type="tip" text="6.54" /> |
| `Archived` | `Exclude` archived dead letters (the default), `Only` archived dead letters, or `Any` <Badge type="tip" text="6.54" /> |
| `TenantId` | Scope the query to one tenant. Dead letters are stored per database, so for a multi-tenanted message store this routes the query to the tenant's database <Badge type="tip" text="6.54" /> |
| `MessageType`, `ExceptionType`, `ReceivedAt`, `Replayable`, `Range` | Exact match filters on the stored columns |
| `MessageIds` | Only these dead letters |
| `Headers` | Only dead letters carrying these header values. This is applied in memory after the other criteria |

Here's what working with failure groups looks like:

<!-- snippet: sample_managing_dead_letters_as_failure_groups -->
<a id='snippet-sample_managing_dead_letters_as_failure_groups'></a>
```cs
var deadLetters = store.DeadLetters;

// One row per failure group, with the first and last time it happened
var groups = await deadLetters.SummarizeAllAsync("MyService",
    new DeadLetterSummaryOptions { Grouping = DeadLetterGrouping.FailureGroup }, token);

foreach (var group in groups)
{
    Console.WriteLine($"{group.Title}: {group.Count} between {group.FirstFailedAt} and {group.LastFailedAt}");
}

var worst = groups.OrderByDescending(x => x.Count).First();

// After deploying a fix, replay that whole group and report how many messages went back
var replayed = await deadLetters.ReplayMatchingAsync(
    new DeadLetterEnvelopeQuery { FailureGroup = worst.Key }, token);
Console.WriteLine($"Replayed {replayed.Affected} message(s)");

// Or set a group aside without deleting it. Archived dead letters drop out of
// summaries, queries and replays until they are restored
if (deadLetters.Capabilities.HasFlag(DeadLetterCapabilities.ArchiveAndRestore))
{
    await deadLetters.ArchiveMatchingAsync(new DeadLetterEnvelopeQuery
    {
        ExceptionMessage = "is not a valid postal code",
        ExceptionMessageMatch = DeadLetterMessageMatch.Contains
    }, token);

    await deadLetters.RestoreMatchingAsync(new DeadLetterEnvelopeQuery
    {
        FailureGroup = worst.Key
    }, token);
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/CoreTests/Persistence/dead_letter_failure_group_samples.cs#L47-L83' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_managing_dead_letters_as_failure_groups' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

### Summaries

`SummarizeAllAsync(serviceName, DeadLetterSummaryOptions, token)` groups dead letters by `DeadLetterGrouping.FailureGroup`,
`MessageType`, `Endpoint` or `ExceptionType`, filtered by `DeadLetterSummaryOptions.Query`. Each `DeadLetterGroupSummary`
carries the group `Key`, its `Count`, the `Title` and `ExceptionType` of a failure group, and the `FirstFailedAt` and
`LastFailedAt` times across the group. `FirstFailedAt` counts failures from before a message was replayed.

The original `SummarizeAllAsync(serviceName, TimeRange, token)` overload is unchanged, except that archived dead letters
are left out.

### Counts from Bulk Operations

`ReplayMatchingAsync(query)` and `DiscardMatchingAsync(query)` do the same thing as `ReplayAsync()` and
`DiscardAsync()`, but return a `DeadLetterOperationResult` whose `Affected` property says how many dead letters were
replayed or discarded. On the relational stores this is the row count of the single set based statement, or the sum
over every batch when the query has a `Headers` filter. A store that can't count reports `null`.

### Replay History

When a message is replayed out of dead letter storage and fails again, the new dead letter remembers its history.
`DeadLetterEnvelope.ReplayCount` says how many times it has been replayed, `LastReplayedAt` when that last happened,
and `FirstFailedAt` when it first failed. `FailedAt` is the time of the latest failure.

The relational stores keep this history in a small `wolverine_dead_letter_history` table, written when the durability
agent moves replayable dead letters back to the inbox. History older than the dead letter expiration window (or the
archived retention, if that's longer) is cleaned up by the same background process that expires dead letters.

### Archiving and Restoring

Sometimes you want a failure group out of the way without deleting the messages. `ArchiveMatchingAsync(query)` sets
dead letters aside, and `RestoreMatchingAsync(query)` brings them back. Both report how many dead letters they affected.

Archived dead letters are left out of summaries, queries and replays unless you ask for them with
`DeadLetterEnvelopeQuery.Archived`. Replaying never touches an archived dead letter, so restore it first. Discarding
by `MessageIds` does reach archived dead letters.

By default, archived dead letters follow the same [expiration](#dead-letter-expiration) rules as any other dead letter.
Set `opts.Durability.ArchivedDeadLetterRetention` to delete archived dead letters once that long has passed since they
were archived instead. That works whether or not `DeadLetterQueueExpirationEnabled` is set, and dead letters that aren't
archived keep the normal expiration behavior.

### Store Support

`IDeadLetters.Capabilities` says which of these features a store supports, so a tool can check before offering them:

| Store | Failure groups | Counts | Archive and restore | Replay history | Exception message matching |
|-------|----------------|--------|---------------------|----------------|----------------------------|
| PostgreSQL, SQL Server, SQLite, MySQL, Oracle | Yes | Yes | Yes | Yes | Yes |
| RavenDb, CosmosDb | Yes | Yes | Yes | No | Yes |

`ArchivedDeadLetterRetention` is enforced by the relational stores. For a custom `IDeadLetters` implementation,
`Capabilities` defaults to `None`, the counting operations report `null`, and archiving throws `NotSupportedException`.

::: warning
The relational stores add new nullable columns to `wolverine_dead_letters` (`failure_group`, `failure_title`,
`failed_at`, `first_failed_at`, `replay_count`, `last_replayed_at` and `archived_at`), an index on `failure_group`, and
the new `wolverine_dead_letter_history` table. Like any other Wolverine schema change, these are applied by the normal
schema migration. See the indexing warning above if your dead letter table is very large.
:::

## Integrating Dead Letters REST API into Your Application

Integrating the Dead Letters REST API into your WolverineFX application provides an elegant and powerful way to manage dead letter messages directly through HTTP requests. This capability is crucial for applications that require a robust mechanism for dealing with message processing failures, enabling developers and administrators to query, replay, or delete dead letter messages as needed. Below, we detail how to add this functionality to your application and describe the usage of each endpoint.

To get started, install that Nuget reference:

```bash
dotnet add package WolverineFx.Http
```

### Adding Dead Letters REST API to Your Application

To integrate the Dead Letters REST API into your WolverineFX application, you simply need to register the endpoints in your application's startup process. This is done by calling `app.MapDeadLettersEndpoints();` within the `Configure` method of your `Startup` class or the application initialization block if using minimal API patterns. This method call adds the necessary routes and handlers for dead letter management to your application's routing table.

<!-- snippet: sample_register_dead_letter_endpoints -->
<a id='snippet-sample_register_dead_letter_endpoints'></a>
```cs
app.MapDeadLettersEndpoints()

    // It's a Minimal API endpoint group,
    // so you can add whatever authorization
    // or OpenAPI metadata configuration you need
    // for just these endpoints
    //.RequireAuthorization("Admin")
    ;
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Program.cs#L292-L302' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_register_dead_letter_endpoints' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

### Using the Dead Letters REST API

#### Query Dead Letters Endpoint

- **Path**: `/dead-letters/`
- **Method**: `POST`
- **Request Body**: `DeadLetterEnvelopeGetRequest`
  - `Limit` (uint, default `100`): Number of records to return per page.
  - `PageNumber` (int): Page number for offset-based pagination — pass `0` (or omit) for the first page.
  - `MessageType` (string?): Filter by message type.
  - `ExceptionType` (string?): Filter by exception type.
  - `ExceptionMessage` (string?): Filter by exception message.
  - `ExceptionMessageMatch` (`Exact` / `Contains`?): How `ExceptionMessage` is matched. Omit for the original `LIKE` pattern behavior.
  - `FailureGroup` (string?): Only dead letters in this [failure group](#failure-groups).
  - `Archived` (`Exclude` / `Only` / `Any`?): Which dead letters to return by [archived state](#archiving-and-restoring). Archived dead letters are excluded by default.
  - `Replayable` (bool?): Filter by the replayable flag. Omit (or `null`) for no filtering; `false` returns only envelopes not yet marked replayable; `true` returns only those already queued for replay. Filtering happens in the database so `TotalCount` and paging stay coherent for the subset.
  - `From` (DateTimeOffset?): Start date for fetching records.
  - `Until` (DateTimeOffset?): End date for fetching records.
  - `TenantId` (string?): Tenant identifier for multi-tenancy support.
  - `DatabaseUri` (Uri?): Scope the query to a single message store when the application has multiple (ancillary) stores configured. Omit to query every store.
- **Response**: `IReadOnlyList<DeadLetterEnvelopeResults>` — one entry per matching message store. Each `DeadLetterEnvelopeResults` contains:
  - `TotalCount` (int): Total matching records for the filter in that store.
  - `Envelopes` (`List<DeadLetterEnvelope>`): The page of dead-letter envelopes.
  - `PageNumber` (int): Echo of the requested page number.
  - `DatabaseUri` (Uri?): The URI identifying which message store this result is from.

::: tip Pagination
The pagination model changed from cursor-based (`StartId` / `NextId`) to offset-based (`PageNumber`) in Wolverine 5. Pass `PageNumber` incrementing from `0` to walk through pages; `TotalCount` lets you compute how many pages exist as `ceil(TotalCount / Limit)`.
:::

::: tip Filtering by replayable state <Badge type="tip" text="6.22" />
After deploying a fix, operators commonly want to separate the messages that are still stuck (`"Replayable": false`) from the ones already queued for replay (`"Replayable": true`). Because the predicate is applied in the database rather than in memory after paging, `TotalCount` reflects the filtered subset — so a `false`-only view drives a paginated UI or batch redrive correctly.

```json
{
  "Limit": 50,
  "PageNumber": 0,
  "Replayable": false
}
```
:::

**Request Example**:

```json
{
  "Limit": 50,
  "PageNumber": 0,
  "MessageType": "OrderPlacedEvent",
  "ExceptionType": "InvalidOrderException"
}
```

**Response Example** (one store; multi-store apps return additional array entries with their own `DatabaseUri`):

```json
[
  {
    "TotalCount": 247,
    "PageNumber": 0,
    "DatabaseUri": "postgresql://localhost:5432/orders",
    "Envelopes": [
      {
        "Id": "4e3d5e88-e01f-4bcb-af25-6e4c14b0a867",
        "ExecutionTime": "2026-04-06T12:00:00Z",
        "MessageType": "OrderFailedEvent",
        "ReceivedAt": "rabbitmq://exchange/orders",
        "Source": "OrderService",
        "ExceptionType": "PaymentException",
        "ExceptionMessage": "The payment method provided is invalid.",
        "SentAt": "2026-04-06T12:00:00Z",
        "Replayable": true,
        "Envelope": { /* the raw wire Envelope (headers, body, destination, etc.) */ },
        "Message": {
          "OrderId": 123456,
          "OrderStatus": "Failed",
          "Reason": "Invalid Payment Method"
        }
      },
      {
        "Id": "5f2c3d1e-3f3d-46f9-ba29-dac8e0f9b078",
        "ExecutionTime": null,
        "MessageType": "AccountOverdrawnEvent",
        "ReceivedAt": "rabbitmq://exchange/accounts",
        "Source": "AccountService",
        "ExceptionType": "OverdrawnException",
        "ExceptionMessage": "Account balance cannot be negative.",
        "SentAt": "2026-04-06T15:15:00Z",
        "Replayable": false,
        "Envelope": { /* … */ },
        "Message": {
          "CustomerId": 78910,
          "AccountBalance": -150.75
        }
      }
    ]
  }
]
```

The `Message` property is the deserialized message body — populated when Wolverine's handler graph knows the message type and a matching serializer is registered. The full wire `Envelope` (headers, content type, destination, etc.) is also returned for inspection.

#### Replay Dead Letters Endpoint

- **Path**: `/dead-letters/replay`
- **Method**: `POST`
- **Description**: Marks specified dead letter messages as replayable. This operation signals the system to attempt reprocessing the messages, ideally after the cause of the initial failure has been resolved.
- **Request Body**: `DeadLetterEnvelopeIdsRequest`
  - `Ids` (Guid[]): Identifiers of the dead-letter envelopes to replay.
  - `TenantId` (string?): If set, the replay is scoped to the tenant's message store.

**Request Example**:

```json
{
  "Ids": ["d3b07384-d113-4ec8-98c4-b3bf34e2c572", "d3b07384-d113-4ec8-98c4-b3bf34e2c573"],
  "TenantId": "tenant-a"
}
```

#### Delete Dead Letters Endpoint

- **Path**: `/dead-letters/`
- **Method**: `DELETE`
- **Description**: Permanently removes specified dead letter messages from the system. Use this operation to clear messages that are no longer needed or cannot be successfully reprocessed.
- **Request Body**: `DeadLetterEnvelopeIdsRequest` (same shape as the replay endpoint above).

**Request Example**:

```json
{
  "Ids": ["d3b07384-d113-4ec8-98c4-b3bf34e2c574", "d3b07384-d113-4ec8-98c4-b3bf34e2c575"],
  "TenantId": "tenant-a"
}
```

### Conclusion

By integrating the Dead Letters REST API into your WolverineFX application, you gain fine-grained control over the management of dead letter messages. This feature not only aids in debugging and resolving processing issues but also enhances the overall reliability of your message-driven applications.
