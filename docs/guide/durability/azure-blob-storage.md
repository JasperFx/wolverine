# Azure Blob Storage Integration <Badge type="tip" text="6.32" />

::: tip
`WolverineFx.AzureBlobStorage` does two independent things, and this page is about the first.

1. **Document and saga persistence** — registered entity types are read and written as blobs, so
   `[Entity]` and the `Storage.Store()` / `Storage.Delete()` return values work against a container,
   and a saga can live in one too. That is the rest of this page.
2. **[Claim check](/guide/durability/claim-checks) storage** — off-loading a large *message payload* to
   a container and shipping a token through the broker. Different feature, different problem; see the
   [Azure Blob Storage claim check section](/guide/durability/claim-checks#azure-blob-storage).

The two share nothing but a package and a `BlobServiceClient`. Use either, or both.
:::

::: warning Moved in 6.32
The claim check half used to ship as `WolverineFx.ClaimCheck.AzureBlobStorage`, which is now deprecated.
The types and their namespace are unchanged, so swapping the package reference is the whole migration.
:::

Plenty of real entities are not in any database. An invoice's rendered content, a generated report, a
document scan: they live in a container, addressed by a name the application decides. Without a
persistence provider for that, handlers drop out of Wolverine's [declarative
persistence](/guide/handlers/persistence) entirely — inject a client, `await` it, and re-implement the
"what if it is not there" half by hand in every handler that reads it.

This package teaches Wolverine to read and write registered types as blobs, so a plain `[Entity]`
parameter and the declarative `Storage.Store()` / `Storage.Delete()` return values work against a
container.

```sh
dotnet add package WolverineFx.AzureBlobStorage
```

## Registering documents

```csharp
builder.Services.AddSingleton(new BlobServiceClient(/* ... */));

builder.Host.UseWolverine(opts =>
{
    opts.UseAzureBlobStoragePersistence(blobs =>
    {
        blobs.Store<InvoiceContent>(x =>
        {
            x.ContainerName = "invoice-content";
            x.BlobNameFor = ctx => $"invoices/v7/{ctx.TenantId}/{ctx.Id}.json";
        });
    });
});
```

`BlobServiceClient` comes from your own registration — directly, or through
`services.AddAzureClients(x => x.AddBlobServiceClient(...))` — so the client keeps whatever credential
pipeline, endpoint and retry policy the rest of your application uses. Wolverine never creates the
container.

Registration is explicit, type by type, and both `ContainerName` and `BlobNameFor` are **required**.
There is deliberately no default blob name layout: the identity-to-name mapping is the part only the
application knows, and a convention Wolverine owned would not survive contact with a container that
already exists.

Explicit registration is also what keeps this provider **selective**. Wolverine resolves an entity to
the first persistence provider that claims its type, so a provider that claimed anything an object
store could theoretically hold would compete with Marten or EF Core for their own documents. This one
claims only what you registered, and is otherwise invisible.

### The blob name function

`BlobNameFor` receives a `BlobNameContext` — the entity type, the resolved identity, and the tenant —
and returns the blob name. That is enough for the layouts real containers actually use:

```csharp
x.BlobNameFor = ctx => $"invoices/v{Generation}/{ctx.TenantId}/{Sanitize(ctx.Id)}.json.br";
```

`TenantId` is null when there is no tenant; Wolverine's default-tenant sentinel is normalised away, so
a blob name function never has to know it exists.

### Serialization

Documents are written as `System.Text.Json` by default. Ask for compression, and the blob's
`Content-Encoding` is set to match:

```csharp
x.Serializer = new BlobDocumentSerializer(compression: BlobCompression.Brotli);
```

For anything else — a different format, an envelope around the payload, a checksum, encryption —
implement `IBlobDocumentSerializer` and set it on the mapping.

## Reading a document

A registered type resolves through `[Entity]` like any other, keeping `Required`, `OnMissing` and
`MissingMessage`:

```csharp
[WolverineGet("/api/invoices/{id}/content")]
public static InvoiceContent Get(
    [Entity(OnMissing = OnMissing.ProblemDetailsWith404,
        MissingMessage = "That invoice's content has not been written yet")]
    InvoiceContent content) => content;
```

The identity is discovered exactly as it is for any other store — the route argument, query string or
message member named `Id` or `{TypeName}Id`. Its type comes from the document's own identity member,
or from `IdentityType` on the mapping if you set it.

A missing *blob* reads as a missing document. A missing *container* does not: it throws, because a
mistyped container name and a document that has not been written yet are different problems and should
not look alike.

## Writing a document

The declarative storage return values work unchanged:

```csharp
public static IStorageAction<InvoiceContent> Handle(RenderInvoice command)
{
    return Storage.Store(new InvoiceContent(command.Id, command.Body));
}
```

`Insert`, `Update` and `Store` are all the same write: an unconditional upload overwrites whatever is
at the blob name, so Blob Storage has no insert-versus-update to honour and these are last-write-wins.

You can also take `IBlobDocumentSession` directly for the same name mapping outside a handler.

## Sagas <Badge type="tip" text="6.32" />

A saga is registered separately from a document, and the difference is not bookkeeping:

```csharp
opts.UseAzureBlobStoragePersistence(blobs =>
{
    blobs.Saga<OrderSaga>(x =>
    {
        x.ContainerName = "order-sagas";
        x.BlobNameFor = ctx => $"sagas/{ctx.TenantId}/{ctx.Id}.json";
    });
});
```

**Saga writes are conditional; document writes are not.** A document is last-write-wins, because an
unconditional upload overwrites whatever is at the blob name. A saga is a read-modify-write, so two
messages for the same saga arriving at once would silently lose one update. Wolverine writes a saga
with Blob Storage's conditional upload instead — `If-None-Match: *` when starting one, `If-Match`
against the ETag it read when updating one — and turns the failure into `SagaConcurrencyException`,
which is the same exception Marten, EF Core and CosmosDB raise for the same situation. A single
`OnException<ConcurrencyException>().RetryTimes(...)` policy covers all of them.

Blob Storage reports the two failures with different statuses — a refused `If-None-Match: *` is `409
BlobAlreadyExists`, a refused `If-Match` is `412 ConditionNotMet` — and both are translated. A saga
another message completed while this one held it is also a `412` rather than a `404`, so completing a
saga twice concurrently is a concurrency failure rather than a resurrection.

`Store<T>()` **refuses** a type deriving from `Saga`, and `Saga<T>()` refuses one that does not. The
two are not interchangeable: a saga registered as a document would be claimed for its *type* and not
for its *chain*, so Wolverine would keep it in the in-memory saga persistor while the container and
blob name function sat unused.

Everything else about sagas is unchanged — `[SagaIdentity]`, `MarkCompleted()`, timeout messages, and
identities of `string`, `Guid`, `int` or `long` all behave exactly as they do on any other store.

## Alongside another store

Blob documents and Marten (or Polecat, Fisher, EF Core, RavenDb, CosmosDb) documents coexist in one
application, and one handler can take an `[Entity]` of each:

```csharp
public static IMartenOp Handle(
    ApproveInvoice command,
    [Entity] Invoice invoice,          // Marten
    [Entity] InvoiceContent content)   // Azure Blob Storage
{
    // ...
}
```

Nothing has to be said to make that work. Wolverine resolves each entity type to the first
persistence provider that claims it, consulting **selective** providers ahead of catch-all document
stores. This provider is selective, so a type you registered with `Store<T>()` resolves to Blob
Storage and everything else falls through to whichever store would have had it anyway -- whichever
order the two integrations were registered in.

Two consequences worth knowing:

- **Sagas belong to whoever was asked for them.** Saga chains resolve on a different question — which
  provider owns this *chain* rather than which owns this *type* — and this provider claims a chain only
  for a saga you registered with `Saga<T>()`. A saga you did not register that way is persisted by
  Marten or your database exactly as before.
- **There is no atomicity across the two.** The Marten write commits in its transaction; the blob write
  already happened. A handler that writes both and then throws has written the blob and not the Marten
  document. If that matters, write the blob from a projection or a follow-on message triggered by the
  committed Marten write, rather than in the same handler.

## What this deliberately does not do

**No message store.** Blob Storage is a poor transactional inbox and outbox, and this package does not
offer to be one. Durability stays with Postgres, SQL Server, Marten or whichever database you already
use; blob documents sit alongside it.

**No unit of work.** Blob Storage has no transaction spanning blobs, so every write takes effect
immediately. A handler that writes two documents and then throws has written one of them. Wolverine
will not pretend otherwise: there is nothing to commit and nothing to roll back. The conditional write
that guards a saga is a per-blob compare-and-swap, not a transaction.

**No querying.** `[All]`, `[FirstOrDefault]` and `[Queryable]` are not supported and fail at
bootstrapping naming this provider. Listing blobs under a name prefix is a paged scan, not a query,
and a scan that looks like a query is worse than an honest refusal.

**No soft delete.** `MaybeSoftDeleted` does not apply. Only your serializer knows what deleted means
for its payload; answer `null` for anything that should count as missing.
