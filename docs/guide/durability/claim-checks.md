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

`[Blob]` is supported on properties typed as `byte[]`, `ReadOnlyMemory<byte>`, `System.IO.Stream`, or `string`. Use the constructor argument to declare a MIME content type that the storage backend can preserve:

```csharp
public record CreateInvoice(
    [property: Blob("application/pdf")] byte[] Pdf,
    string Reference);
```

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

## Backends

Wolverine ships two production-grade storage backends as separate NuGet packages.

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
dotnet add package WolverineFx.ClaimCheck.AmazonS3
```

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

### File system (built in)

For local development, integration tests, or single-node deployments you can use the bundled `FileSystemClaimCheckStore` directly:

```csharp
opts.UseClaimCheck(cc => cc.UseFileSystem("/var/wolverine/claim-checks"));
```

Each payload is written as `{id}.bin`, with a sidecar `{id}.meta` file recording the original content type so the round-trip is lossless even if the token were ever reconstructed externally.

## Operational considerations

- **Lifetime of stored payloads.** The pipeline never auto-deletes blobs. If you let large payloads accumulate, they will eat storage. The recommended pattern is to use the storage system's native lifecycle support (S3 lifecycle rules, Azure Blob Storage lifecycle policies, or a periodic cleanup job for the file system backend) keyed off blob age. A future enhancement may add Wolverine-driven TTL; tracked separately.
- **Synchronous serializer hot path.** `IMessageSerializer.Write` and `IMessageSerializer.ReadFromData` are synchronous. When the inner serializer is `IAsyncMessageSerializer` (most are), the pipeline preserves async end-to-end. If your inner serializer is sync-only, the upload/download will block on the hot path; pre-uploading payloads outside the serializer is an option for very high-throughput scenarios.
- **Backend failures.** If the store is unreachable on send, the publish fails and Wolverine's normal retry/dead-letter machinery applies. If the store is unreachable on receive, the handler chain throws and the message is retried per its failure rules — the same behavior as if the original payload were corrupted in transport.
- **Tokens are opaque.** Don't parse `ClaimCheckToken.Id`. Backends are free to use whatever id format makes sense (`Guid.ToString("N")` for the bundled stores).

## Issue tracking

This feature was originally tracked in [#2412](https://github.com/JasperFx/wolverine/issues/2412).
