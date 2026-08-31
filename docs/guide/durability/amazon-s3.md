# Amazon S3 Integration <Badge type="tip" text="6.31" />

::: tip
`WolverineFx.AmazonS3` does two independent things, and this page is about the first.

1. **Document persistence** — registered entity types are read and written as S3 objects, so `[Entity]`
   and the `Storage.Store()` / `Storage.Delete()` return values work against a bucket. That is the rest
   of this page.
2. **[Claim check](/guide/durability/claim-checks) storage** — off-loading a large *message payload* to
   a bucket and shipping a token through the broker. Different feature, different problem; see the
   [Amazon S3 claim check section](/guide/durability/claim-checks#amazon-s3).

The two share nothing but a package and an `IAmazonS3`. Use either, or both.

Sagas are **not** persisted to S3. A saga belongs to whichever transactional store owns its chain —
see [alongside another store](#alongside-another-store).
:::

::: warning Moved in 6.31
The claim check half used to ship as `WolverineFx.ClaimCheck.AmazonS3`, which is now deprecated.
The types and their namespace are unchanged, so swapping the package reference is the whole migration.
:::

Plenty of real entities are not in any database. An invoice's rendered content, a generated report, a
document scan: they live in a bucket, addressed by a key the application decides. Without a
persistence provider for that, handlers drop out of Wolverine's [declarative
persistence](/guide/handlers/persistence) entirely — inject a client, `await` it, and re-implement the
"what if it is not there" half by hand in every handler that reads it.

This package teaches Wolverine to read and write registered types as S3 objects, so a plain `[Entity]`
parameter and the declarative `Storage.Store()` / `Storage.Delete()` return values work against a
bucket.

```sh
dotnet add package WolverineFx.AmazonS3
```

## Registering documents

```csharp
builder.Services.AddSingleton<IAmazonS3>(sp => new AmazonS3Client(/* ... */));

builder.Host.UseWolverine(opts =>
{
    opts.UseAmazonS3Persistence(s3 =>
    {
        s3.Store<InvoiceContent>(x =>
        {
            x.BucketName = "invoice-content";
            x.KeyFor = ctx => $"invoices/v7/{ctx.TenantId}/{ctx.Id}.json";
        });
    });
});
```

`IAmazonS3` comes from your own registration, so the client keeps whatever credential chain, region
and retry policy the rest of your application uses.

Registration is explicit, type by type, and both `BucketName` and `KeyFor` are **required**. There is
deliberately no default key layout: the identity-to-key mapping is the part only the application
knows, and a convention Wolverine owned would not survive contact with a bucket that already exists.

Explicit registration is also what keeps this provider **selective**. Wolverine resolves an entity to
the first persistence provider that claims its type, so a provider that claimed anything an object
store could theoretically hold would compete with Marten or EF Core for their own documents. This one
claims only what you registered, and is otherwise invisible.

### The key function

`KeyFor` receives an `S3KeyContext` — the entity type, the resolved identity, and the tenant — and
returns the object key. That is enough for the keys real buckets actually use:

```csharp
x.KeyFor = ctx => $"invoices/v{Generation}/{ctx.TenantId}/{Sanitize(ctx.Id)}.json.br";
```

`TenantId` is null when there is no tenant; Wolverine's default-tenant sentinel is normalised away, so
a key function never has to know it exists.

### Serialization

Documents are written as `System.Text.Json` by default. Ask for compression, and the object's
`Content-Encoding` is set to match:

```csharp
x.Serializer = new S3DocumentSerializer(compression: S3Compression.Brotli);
```

For anything else — a different format, an envelope around the payload, a checksum, encryption —
implement `IS3DocumentSerializer` and set it on the mapping.

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

## Writing a document

The declarative storage return values work unchanged:

```csharp
public static IStorageAction<InvoiceContent> Handle(RenderInvoice command)
{
    return Storage.Store(new InvoiceContent(command.Id, command.Body));
}
```

`Insert`, `Update` and `Store` are all the same write: a `PutObject` overwrites whatever is at the
key, so S3 has no insert-versus-update to honour and these are last-write-wins.

You can also take `IS3DocumentSession` directly for the same key mapping outside a handler.

## Alongside another store

S3 documents and Marten (or Polecat, Fisher, EF Core, RavenDb, CosmosDb) documents coexist in one
application, and one handler can take an `[Entity]` of each:

```csharp
public static IMartenOp Handle(
    ApproveInvoice command,
    [Entity] Invoice invoice,          // Marten
    [Entity] InvoiceContent content)   // S3
{
    // ...
}
```

Nothing has to be said to make that work. Wolverine resolves each entity type to the first
persistence provider that claims it, consulting **selective** providers ahead of catch-all document
stores. This provider is selective, so a type you registered with `Store<T>()` resolves to S3 and
everything else falls through to whichever store would have had it anyway -- whichever order the two
integrations were registered in.

Two consequences worth knowing:

- **Sagas stay with the transactional store.** Saga chains resolve on a different question -- which
  provider owns this *chain* rather than which owns this *type* -- and this provider claims no chains,
  so a saga in a mixed application is persisted by Marten or your database exactly as before.
- **There is no atomicity across the two.** The Marten write commits in its transaction; the S3 write
  already happened. A handler that writes both and then throws has written the S3 object and not the
  Marten document. If that matters, write the S3 object from a projection or a follow-on message
  triggered by the committed Marten write, rather than in the same handler.

## What this deliberately does not do

**No message store.** S3 is a poor transactional inbox and outbox, and this package does not offer to
be one. Durability stays with Postgres, SQL Server, Marten or whichever database you already use;
S3 documents sit alongside it.

**No unit of work.** S3 has no transaction, so every write takes effect immediately. A handler that
writes two documents and then throws has written one of them. Wolverine will not pretend otherwise:
there is nothing to commit and nothing to roll back.

**No querying.** `[All]`, `[FirstOrDefault]` and `[Queryable]` are not supported and fail at
bootstrapping naming this provider. `ListObjectsV2` over a key prefix is a paged scan, not a query,
and a scan that looks like a query is worse than an honest refusal.

**No sagas.** `Store<T>()` refuses a type deriving from `Saga`. It would otherwise be silently useless
*and* dangerous: a saga chain picks its persistence on which provider owns the chain, this one owns
none, and the fallback is the **in-memory** saga persistor -- so the bucket and key function would be
ignored and the saga would live in process memory, gone on the next restart and invisible to every
other node. Keep sagas with a transactional store.

**No soft delete.** `MaybeSoftDeleted` does not apply. Only your serializer knows what deleted means
for its payload; answer `null` for anything that should count as missing.
