# Claim Checks

Some messages carry payloads that are too large to send efficiently through a message broker — multi-megabyte attachments, screenshots, blob exports, generated documents. Pushing those bytes through RabbitMQ, Azure Service Bus, SQS, or any other transport that has practical message-size limits hurts throughput, raises broker storage costs, and can fail outright once a single message crosses the broker's hard limit.

The classic solution is the [Claim Check / Data Bus pattern](https://www.enterpriseintegrationpatterns.com/patterns/messaging/StoreInLibrary.html): store the payload in shared external storage (a blob store, object store, or even a network share), pass a small reference token through the message transport, and re-hydrate the payload on the receiving side. Wolverine ships first-class support for this pattern with a pluggable storage backend.

## How it works

Mark properties on a message that should be off-loaded with the `[Blob]` attribute (`Wolverine.Persistence.BlobAttribute`):

<<< @/../src/Testing/CoreTests/Persistence/ClaimCheck/Messages.cs#sample_blob_attribute_message

When `opts.UseClaimCheck(...)` is configured (see below), every send and receive runs the message through a small decorator on the configured `IMessageSerializer`:

- **Outgoing**: each `[Blob]`-marked property is uploaded to the configured `IClaimCheckStore`. The original property is set to `null` (or `ReadOnlyMemory<byte>.Empty`) so the serialized envelope body stays small. A header named `claim-check.{PropertyName}` carrying the token is written onto the envelope.
- **Incoming**: after the inner serializer reconstructs the message, the decorator inspects the same headers, fetches each payload back out of the store, and writes the bytes back onto the message before the handler runs.

The handler sees a fully populated message — it never has to know that the bytes traveled out of band.

Once the envelope body has been serialized, the decorator **restores** each off-loaded property back onto the in-memory message. The bytes already placed on the bus are unaffected — they still carry only the claim-check token — but the live message object is left intact rather than mutated. This matters for **in-process routing**: a local queue can hand the *same* message instance to the handler without a serialize → deserialize round trip, so restoring the off-loaded properties is what guarantees the handler still sees the full payload.

`[Blob]` is supported on properties typed as `byte[]`, `ReadOnlyMemory<byte>`, `System.IO.Stream`, or `string`. Use the constructor argument to declare a MIME content type that the storage backend can preserve:

```csharp
public record CreateInvoice(
    [property: Blob("application/pdf")] byte[] Pdf,
    string Reference);
```

A `[Blob]`-marked `System.IO.Stream` is read fully into memory to off-load it, and is re-materialized as a fresh, read-only `MemoryStream` on the receiving side (and on the in-process restore described above). Don't assume the handler receives the original stream implementation or that it is positioned anywhere other than the start.

## Core abstractions

The pattern is built on three small types in `Wolverine.Persistence`:

### `IClaimCheckStore`

The pluggable backend contract. Implementations persist a payload, return an opaque `ClaimCheckToken` that subsequent loads will use to refer back to it, and support best-effort delete.

<<< @/../src/Wolverine/Persistence/ClaimCheck/IClaimCheckStore.cs

### `ClaimCheckToken`

A small record that captures the backend's payload id, the MIME content type, and the size in bytes. Tokens are wire-encoded as a single string into the envelope header so they round-trip cleanly through any transport without requiring transport-specific support.

```csharp
public record ClaimCheckToken(string Id, string ContentType, long Length);
```

### `[Blob]` attribute

Applied to message properties that should be off-loaded. Constructor accepts the MIME content type (defaults to `application/octet-stream`).

## Configuration

Enable the pipeline once on `WolverineOptions`:

```csharp
using Wolverine.Persistence; // brings in UseClaimCheck

builder.Host.UseWolverine(opts =>
{
    opts.UseClaimCheck(claimCheck =>
    {
        // Pick a backend; see below.
    });
});
```

When `UseClaimCheck(...)` runs without an explicit `Store`, the pipeline falls back to a `FileSystemClaimCheckStore` rooted at `Path.GetTempPath()/wolverine-claim-check`. That default is fine for local development and integration tests but is not appropriate across multiple machines — production deployments should pick one of the shared-storage backends below.

`UseClaimCheck` is idempotent: calling it again replaces the store on the existing decorator without double-wrapping the serializer.

`IClaimCheckStore` is registered as a singleton in DI, so any handler that needs to upload or fetch payloads explicitly can take it as a constructor dependency.

### Size-threshold auto-offload <Badge type="tip" text="6.22" />

`[Blob]` is opt-in per property. The common failure mode it doesn't cover is *forgetting* the attribute on a property that occasionally gets large, and only discovering it when a message slams into the broker's hard size limit (SQS 1 MiB, Azure Service Bus standard 256 KB, Kafka default `message.max.bytes` ~1 MB).

Set a size threshold as a safety net. When the **serialized body** of any outgoing message exceeds it, Wolverine off-loads the **entire body** to the configured store and replaces it on the wire with a single reference header — no `[Blob]` required. The receiving side pulls the body back from the store before deserializing, transparently:

```csharp
opts.UseClaimCheck(claimCheck =>
{
    claimCheck.UseAmazonS3FromServices(bucketName: "wolverine-claim-checks");

    // Anything whose serialized body is larger than 200 KB is off-loaded whole,
    // even if no property is marked [Blob].
    claimCheck.AutoOffloadPayloadsLargerThan(200 * 1024);
});
```

The threshold is measured **after** any `[Blob]` properties have already been off-loaded, so it reflects the body that would actually go on the wire. The two mechanisms compose: `[Blob]` is the explicit per-property path, and the threshold is the whole-body backstop. Leaving `AutoOffloadThreshold` unset (the default) disables auto-offload entirely.

### Per-message / per-endpoint store selection <Badge type="tip" text="6.22" />

`UseClaimCheck(...)` configures one global store, but you can route individual messages (or whole endpoints) to different backends — S3 for one, Azure Blob for another, database-LOB for a third — and override the threshold per route. `Store` remains the default when no route matches:

```csharp
opts.UseClaimCheck(claimCheck =>
{
    claimCheck.Store = defaultStore;                       // fallback for everything else
    claimCheck.AutoOffloadPayloadsLargerThan(256 * 1024);  // global default threshold

    // A specific message type → a specific store (+ its own threshold)
    claimCheck.StoreForMessage<ExportGenerated>(s3Store, autoOffloadThreshold: 1024 * 1024);

    // Any message type matching a predicate
    claimCheck.StoreForMessages(t => t.Namespace!.StartsWith("Acme.Media"), azureStore);

    // Route by the outgoing envelope — e.g. everything headed to a particular endpoint
    claimCheck.StoreWhen(env => env.Destination?.Scheme == "rabbitmq", dbLobStore);
});
```

Routes are evaluated in registration order; the first match wins. The store a payload was off-loaded to is recorded in a `claim-check.$store` header, so the **receiver loads from the same backend even though its listening endpoint URI differs from the sender's** — this is what makes `StoreWhen` (endpoint-based) routing round-trip. Envelopes that used the default store carry no such header, so single-store apps are byte-for-byte unchanged.

::: warning Both nodes must share the configuration
The receiver resolves the store by the key the sender stamped, so it must register the same routes. `StoreForMessage<T>` keys off the message type name (order-independent); `StoreForMessages` / `StoreWhen` use positional keys, so register them in the same order on every host. An envelope that references an unknown store key fails fast with a clear error rather than silently loading from the wrong place.
:::

## Backends

Wolverine ships several production-grade storage backends as separate NuGet packages.

### Azure Blob Storage

```sh
dotnet add package WolverineFx.ClaimCheck.AzureBlobStorage
```

```csharp
using Wolverine.ClaimCheck.AzureBlobStorage;

builder.Host.UseWolverine(opts =>
{
    opts.UseClaimCheck(cc => cc.UseAzureBlobStorage(
        connectionString: builder.Configuration.GetConnectionString("AzureStorage")!,
        containerName: "wolverine-claim-checks"));
});
```

Or hand the store an existing `BlobContainerClient` if you want to control the credential pipeline yourself:

```csharp
opts.UseClaimCheck(cc => cc.UseAzureBlobStorage(myContainerClient));
```

The store maps each `ClaimCheckToken.Id` directly to a blob name, and sets `BlobHttpHeaders.ContentType` from the token so the blob is browseable in the Azure portal with the right MIME type. `DeleteAsync` is idempotent (uses `DeleteIfExistsAsync`), so retries and crash-recovery flows are safe.

### Amazon S3

```sh
dotnet add package WolverineFx.AmazonS3
```

::: warning Moved in 6.32
The S3 claim check store now ships in `WolverineFx.AmazonS3`, alongside the
[S3 document persistence](/guide/durability/amazon-s3). **`WolverineFx.ClaimCheck.AmazonS3` is deprecated**
and will not be published again.

Nothing in your code changes -- the types and their namespace are the same. Swap the package reference,
and remove the old one rather than keeping both: two packages carrying the same types in the same
namespace produce ambiguous-reference compiler errors.

The other object store backends -- Azure Blob Storage, Google Cloud Storage and NATS -- are unaffected
and stay in the `WolverineFx.ClaimCheck.*` family.
:::

```csharp
using Wolverine.ClaimCheck.AmazonS3;

builder.Services.AddSingleton<IAmazonS3>(sp => new AmazonS3Client(/* ... */));

builder.Host.UseWolverine(opts =>
{
    opts.UseClaimCheck(cc => cc.UseAmazonS3FromServices(bucketName: "wolverine-claim-checks"));
});
```

The `UseAmazonS3FromServices` overload defers `IAmazonS3` resolution until the container is built, which lets you reuse whatever client your application already configures (with its credential chain, retry policy, region, etc.). For tests and one-off setups, an explicit-client overload is also available:

```csharp
opts.UseClaimCheck(cc => cc.UseAmazonS3(myS3Client, bucketName: "wolverine-claim-checks"));
```

Token id maps to the object key. The supplied content type is set as `PutObjectRequest.ContentType`, which preserves the MIME type for downloads and S3 lifecycle policies. `DeleteAsync` is naturally idempotent — S3 returns success even when the key is absent.

### Google Cloud Storage

```sh
dotnet add package WolverineFx.ClaimCheck.GoogleCloudStorage
```

```csharp
using Google.Cloud.Storage.V1;
using Wolverine.ClaimCheck.GoogleCloudStorage;

builder.Services.AddSingleton(StorageClient.Create());

builder.Host.UseWolverine(opts =>
{
    opts.UseClaimCheck(cc => cc.UseGoogleCloudStorageFromServices(bucketName: "wolverine-claim-checks"));
});
```

The `UseGoogleCloudStorageFromServices` overload defers `StorageClient` resolution until the container is built, mirroring the S3 pattern. An explicit-client overload is also available for tests and one-off setups:

```csharp
opts.UseClaimCheck(cc => cc.UseGoogleCloudStorage(myStorageClient, bucketName: "wolverine-claim-checks"));
```

Token id maps to the object name, and the supplied content type is set on the object so it downloads with the right MIME type and participates in GCS lifecycle rules. `DeleteAsync` is idempotent — a `404 Not Found` on a missing object is swallowed.

### NATS JetStream Object Store

For applications already using NATS — especially the [Wolverine NATS transport](/guide/messaging/transports/nats) — the NATS [JetStream Object Store](https://docs.nats.io/nats-concepts/jetstream/obj_store) backend lets you off-load large payloads without standing up a separate blob or object store. This backend is unique to Wolverine in the .NET messaging space.

```sh
dotnet add package WolverineFx.ClaimCheck.Nats
```

```csharp
using Wolverine.ClaimCheck.Nats;

// Reuse the application's existing, already-connected NATS connection
INatsConnection connection = /* your connected NatsConnection */;

builder.Host.UseWolverine(opts =>
{
    opts.UseClaimCheck(cc => cc.UseNatsObjectStore(connection, bucketName: "wolverine-claim-checks"));
});
```

The server must have JetStream enabled. The object-store bucket is created on first use if it does not already exist. Token id maps to the object name; the content type travels with the token. `DeleteAsync` is idempotent — a missing object is treated as already deleted. An overload accepting an existing `INatsObjContext` is also available if you manage the object-store context yourself.

#### Expiring NATS payloads <Badge type="tip" text="6.30" />

Pass a `maxAge` and Wolverine configures the bucket it creates with a native TTL — the NATS server then
expires off-loaded payloads itself:

```csharp
opts.UseClaimCheck(cc => cc.UseNatsObjectStore(
    connection,
    bucketName: "wolverine-claim-checks",
    maxAge: 7.Days()));
```

This is the option to prefer: expiry is server-side, costs nothing, and keeps working while your
application is down. Note that it only applies to a bucket **Wolverine creates** — an existing bucket keeps
whatever max age it was already configured with.

For a bucket you did not let Wolverine create, this backend also implements
`IClaimCheckStoreWithExpiration`, so [`DeletePayloadsOlderThan(...)`](#expiring-old-payloads) works here
too. Unlike the cloud object stores — where enumerating a bucket means a billed LIST request on every pass,
which is why they deliberately opt out — a NATS listing reads the bucket's local metadata stream, so
sweeping is cheap.

### PostgreSQL (database LOB)

The zero-new-infrastructure option for critter-stack users: off-loaded payloads are stored as `bytea` rows in your existing PostgreSQL database — no S3 / Azure / GCS account required.

```sh
dotnet add package WolverineFx.ClaimCheck.Postgresql
```

```csharp
using Wolverine.ClaimCheck.Postgresql;

builder.Host.UseWolverine(opts =>
{
    opts.UseClaimCheck(cc => cc.UsePostgresqlClaimCheck(
        connectionString: builder.Configuration.GetConnectionString("Postgres")!,
        schemaName: "public",
        tableName: "wolverine_claim_check"));
});
```

An overload accepting an existing `NpgsqlDataSource` is also available if you want to reuse the data source your application already configures:

```csharp
opts.UseClaimCheck(cc => cc.UsePostgresqlClaimCheck(myDataSource));
```

The claim check table is created on first use (`create schema/table if not exists`). Token id maps to the row's primary key; the content type and length are stored alongside the `bytea` body. `DeleteAsync` is naturally idempotent — deleting a missing row is a no-op. This backend supports Wolverine-driven expiration — see [Expiring old payloads](#expiring-old-payloads) — and because the payloads live in a table you own, database-native cleanup (a scheduled `delete ... where created < ...`) works too.

### SQL Server (database LOB)

The SQL Server sibling of the PostgreSQL backend: off-loaded payloads are stored as `varbinary(max)` rows
in your existing SQL Server database.

```sh
dotnet add package WolverineFx.ClaimCheck.SqlServer
```

```csharp
using Wolverine.ClaimCheck.SqlServer;

builder.Host.UseWolverine(opts =>
{
    opts.UseClaimCheck(cc => cc.UseSqlServerClaimCheck(
        connectionString: builder.Configuration.GetConnectionString("SqlServer")!,
        schemaName: "dbo",
        tableName: "wolverine_claim_check"));
});
```

An overload accepting a connection factory is available when connections need custom construction, for
example an access-token credential:

```csharp
opts.UseClaimCheck(cc => cc.UseSqlServerClaimCheck(async token =>
{
    var conn = new SqlConnection(connectionString) { AccessToken = await getTokenAsync() };
    await conn.OpenAsync(token);
    return conn;
}));
```

The schema, table, and expiration index are created on first use. Token id maps to the row's primary key,
and `DeleteAsync` is naturally idempotent. This backend supports
[Wolverine-driven expiration](#expiring-old-payloads).

Schema and table names must be **simple identifiers**. A dotted value such as `crm.sales` is rejected
outright rather than being silently treated as a multi-part name.

### Marten (database LOB)

For critter-stack applications already using Marten, this stores off-loaded payloads in the database Marten
is already connected to — no connection string to configure, no second database, no object store.

```sh
dotnet add package WolverineFx.ClaimCheck.Marten
```

```csharp
using Wolverine.ClaimCheck.Marten;

builder.Services.AddMarten(/* ... */).IntegrateWithWolverine();

builder.Host.UseWolverine(opts =>
{
    opts.UseClaimCheck(cc => cc.UseMartenClaimCheck());
});
```

The payload table is created on first use in Marten's own `DatabaseSchemaName` (override with the
`schemaName` / `tableName` arguments), and the backend supports
[Wolverine-driven expiration](#expiring-old-payloads).

::: tip Payloads are `bytea` rows, not Marten documents
This backend deliberately does *not* store payloads as Marten documents. A Marten document is JSONB, which
base64-encodes a binary body for roughly 33% storage overhead plus encode/decode cost on every payload —
exactly the wrong trade for the large-payload workload claim checks exist to serve. What it takes from
Marten is the *connectivity and schema*, which is what "zero new infrastructure" actually means here.

One consequence: because the payload table is not a document, it does not take part in Marten's schema
management or appear in `IDocumentStore.Advanced` operations. It is created lazily, exactly like the
standalone PostgreSQL backend's table.
:::

With separate-database (conjoined) tenancy the payloads go in the **master** database. Claim-check tokens
are opaque and carry no tenant semantics, so scattering payloads across tenant databases would leave a
receiving node unable to tell which one to read from.

### File system (built in)

For local development, integration tests, or single-node deployments you can use the bundled `FileSystemClaimCheckStore` directly:

```csharp
opts.UseClaimCheck(cc => cc.UseFileSystem("/var/wolverine/claim-checks"));
```

Each payload is written as `{id}.bin`, with a sidecar `{id}.meta` file recording the original content type so the round-trip is lossless even if the token were ever reconstructed externally.

## Consuming MassTransit MessageData

If you are migrating off MassTransit, or running Wolverine and MassTransit services side by side, Wolverine
can read MassTransit's [`MessageData<T>`](https://masstransit.io/documentation/patterns/claim-check)
claim-check references directly:

```csharp
opts.UseRabbitMq(/* ... */)
    .UseConventionalRouting();

opts.PublishAllMessages().ToRabbitQueue("large-docs")
    .UseMassTransitInterop(mt =>
    {
        // Point Wolverine's store at the SAME bucket MassTransit's repository writes to
        mt.ReadMessageDataFrom(new AmazonS3ClaimCheckStore(s3Client, "mt-message-data"));
    });
```

Any property marked with `[Blob]` on the incoming message is then hydrated from that store. `[Blob]` is
reused as the opt-in marker deliberately — a property big enough for MassTransit to have off-loaded is the
same property Wolverine would off-load, so a contract shared across the migration needs no extra
annotation. It also matters for correctness: a blanket rule over every `byte[]` or `string` property would
try to read ordinary values as claim-check references.

::: warning MassTransit's reference is in the body, not a header
Wolverine carries its own claim-check token in an envelope header. MassTransit does not — it writes a JSON
object *inside the message body*, of the form `{ "data-ref": "…", "text": "…", "data": "…" }`. `text` and
`data` are the inline forms MassTransit uses for payloads under its 4&nbsp;KB threshold; when either is
present Wolverine uses it directly and never calls the store.
:::

Wolverine understands the address formats produced by MassTransit's file-system, Amazon S3, and Azure
Storage repositories:

| MassTransit repository | Address | Payload id |
| --- | --- | --- |
| File system / Amazon S3 | `urn:file:{key}` (colons for separators) | segments rejoined with `/` |
| Azure Storage | `https://…/{container}/{blob}` | everything after the container segment |
| In-memory | `urn:msgdata:{id}` | rejected — see below |

An Azure blob name ending in `.gz` is gunzipped automatically, matching that repository's compression
option. For a custom `IMessageDataRepository` with its own address format, pass a mapper:

```csharp
mt.ReadMessageDataFrom(store, address => address.Segments.Last());
```

Two limits worth knowing up front:

- **The address carries the key, not the bucket.** MassTransit's repository configuration owns the
  bucket/container, so it never appears in the address. The store you pass to `ReadMessageDataFrom` must be
  pointed at the same one, or every lookup will miss.
- **MassTransit's in-memory repository cannot be read at all.** Its payloads never leave the producing
  process. Wolverine fails with an explicit message saying so rather than a confusing lookup failure.

This is a **read/consume path only** — Wolverine does not produce MassTransit-compatible references, and
the outbound path is unchanged by enabling it.
## Expiring old payloads

Nothing about a successful send tells Wolverine when the payload behind it stops being needed — the
message may still be scheduled, retrying, or parked in a dead-letter queue. So by default Wolverine
does not delete off-loaded payloads at all, and they accumulate until something else removes them.

There are two ways to close that gap, and for object stores the first is usually the better one:

**1. Native lifecycle rules.** Azure Blob Storage, Amazon S3, and Google Cloud Storage all expire
objects server-side for free. Point a lifecycle policy at the container/bucket (or prefix) your
claim-check store writes to and you are done — no Wolverine configuration, no LIST charges, and it
keeps working even while your application is down. These backends deliberately do **not** implement
Wolverine-driven sweeping for exactly that reason.

**2. A Wolverine-driven sweep.** For backends where enumeration is cheap — the file system store, the
database-LOB stores, and [NATS](#nats-jetstream-object-store) — call `DeletePayloadsOlderThan`:

```csharp
opts.UseClaimCheck(cc =>
{
    cc.UsePostgresqlClaimCheck(connectionString);

    // Delete off-loaded payloads more than seven days old
    cc.DeletePayloadsOlderThan(7.Days());

    // Optional tuning
    cc.SweepInterval = 10.Minutes();   // default
    cc.SweepBatchSize = 1000;          // payloads deleted per store, per pass
});
```

::: warning Size the TTL against your slowest delivery, not your fastest
The sweep deletes purely by age, and it has no idea whether a message that references a payload is
still in flight. The TTL must comfortably exceed the longest window in which a message could still
need to re-hydrate — scheduled delivery, retry back-offs, and time spent sitting in a dead-letter
queue awaiting a replay all count. A payload swept out from under a message that is delivered later
will fail to load.
:::

A few properties worth knowing:

- **It runs on every node, not just the leader.** Deleting by age is idempotent, so overlapping
  sweeps are harmless, and each node jitters its own schedule. This is deliberate: leader election
  requires message persistence, but claim checks do not, so a leader-pinned sweeper would silently
  never run for apps using claim checks without a durable message store.
- **Every configured store is swept**, including any per-message or per-endpoint stores registered
  with `StoreForMessage<T>` / `StoreWhen` — not just the default one.
- **Backends that cannot be swept are skipped with a warning** naming the store type, so a TTL that
  is not actually doing anything is visible in the logs rather than silently ignored.
- **A full batch triggers an immediate follow-up pass**, so a large backlog drains without waiting
  out `SweepInterval` between every batch.

### Writing a sweepable backend

A custom `IClaimCheckStore` opts into sweeping by implementing `IClaimCheckStoreWithExpiration`:

<<< @/../src/Wolverine/Persistence/ClaimCheck/IClaimCheckStoreWithExpiration.cs

Implementations must tolerate concurrent sweeps from several nodes, and deleting an already-deleted
payload must not throw.

## Operational considerations

- **Lifetime of stored payloads.** By default the pipeline only deletes a payload when the send that created it fails outright. Everything successfully sent accumulates until something removes it — either a Wolverine-driven TTL (see [Expiring old payloads](#expiring-old-payloads)) or your storage system's own lifecycle rules.
- **Synchronous serializer hot path.** `IMessageSerializer.Write` and `IMessageSerializer.ReadFromData` are synchronous. When the inner serializer is `IAsyncMessageSerializer` (most are), the pipeline preserves async end-to-end. If your inner serializer is sync-only, the upload/download will block on the hot path; pre-uploading payloads outside the serializer is an option for very high-throughput scenarios.
- **Backend failures.** If the store is unreachable on send, the publish fails and Wolverine's normal retry/dead-letter machinery applies. If the store is unreachable on receive, the handler chain throws and the message is retried per its failure rules — the same behavior as if the original payload were corrupted in transport.
- **Tokens are opaque.** Don't parse `ClaimCheckToken.Id`. Backends are free to use whatever id format makes sense (`Guid.ToString("N")` for the bundled stores).
- **Local queues and in-process routing.** A *durable* local queue serializes the envelope when it persists it, so the off-load fires for it exactly as it would for an external transport. A *buffered* (in-memory) local queue never serializes the local hand-off, so no off-load happens there. Either way the handler receives a fully-populated message: the off-loaded properties are restored on the live message after serialization (see [How it works](#how-it-works)).
- **Off-loading requires an envelope.** The claim-check token is carried in an envelope header, so the off-load only round-trips through Wolverine's normal `Write(envelope)` / `WriteAsync(envelope)` paths. Serializing a `[Blob]` message outside that path — for example a raw `IMessageSerializer.WriteMessage(object)` call with no envelope — cannot carry the token, so the payload would not be recoverable on the other side. That path therefore does not upload anything at all; it clears the `[Blob]` properties so the serialized body stays small, then restores them on the live message.

## Issue tracking

This feature was originally tracked in [#2412](https://github.com/JasperFx/wolverine/issues/2412). The in-process / local-queue re-hydration behavior was fixed in [#3048](https://github.com/JasperFx/wolverine/pull/3048).
