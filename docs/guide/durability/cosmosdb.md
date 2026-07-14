# CosmosDb Integration

Wolverine supports an [Azure CosmosDB](https://learn.microsoft.com/en-us/azure/cosmos-db/) backed message persistence strategy
option as well as CosmosDB-backed transactional middleware and saga persistence. To get started, add the `WolverineFx.CosmosDb` dependency to your application:

```bash
dotnet add package WolverineFx.CosmosDb
```

and in your application, tell Wolverine to use CosmosDB for message persistence:

```cs
var builder = Host.CreateApplicationBuilder();

// Register CosmosClient with DI
builder.Services.AddSingleton(new CosmosClient(
    "your-connection-string",
    new CosmosClientOptions
    {
        // Required if you use CosmosDB saga persistence. See "Serializer Configuration" below
        SerializerOptions = new CosmosSerializationOptions
        {
            PropertyNamingPolicy = CosmosPropertyNamingPolicy.CamelCase
        }
    }
));

builder.UseWolverine(opts =>
{
    // Tell Wolverine to use CosmosDB, specifying the database name
    opts.UseCosmosDbPersistence("your-database-name");

    // The CosmosDB integration supports basic transactional
    // middleware just fine
    opts.Policies.AutoApplyTransactions();
});
```

## Serializer Configuration

::: warning
If you use CosmosDB **saga persistence**, the `CosmosClient` you register **must** be configured to camel
case its property names. Wolverine refuses to start otherwise.
:::

CosmosDB rejects any document that does not carry a lowercase `id` property. The CosmosDB SDK's default
serializer writes .NET property names verbatim, so a saga with the usual PascalCase `Id` is serialized as
`"Id"` — and CosmosDB will not store it. Tell the client to camel case its property names:

```cs
var client = new CosmosClient("your-connection-string", new CosmosClientOptions
{
    SerializerOptions = new CosmosSerializationOptions
    {
        PropertyNamingPolicy = CosmosPropertyNamingPolicy.CamelCase
    }
});
```

Skip this and the first saga persist fails with:

```
400 Bad Request: The input content is invalid because the required properties - 'id; ' - are missing
```

Worse, because the saga was never stored, every follow-up message for that saga then fails with an
`UnknownSagaException` — which looks like an unrelated bug and sends you looking in the wrong place.

To keep that from ever reaching production, Wolverine checks the registered `CosmosClient` at startup: it
serializes a probe instance of each saga with the client's own serializer, and throws an actionable
`InvalidOperationException` naming the offending saga types if the resulting document would have no `id`.
Applications that use CosmosDB only for message persistence are unaffected — every document Wolverine
writes itself already carries an explicit `id` mapping — and start regardless of the naming policy.

If you would rather not change the naming policy for the whole client, map the saga's identity member onto
the document id yourself instead:

```cs
public class OrderSaga : Saga
{
    // The SDK's default serializer is Newtonsoft based, so use Newtonsoft's [JsonProperty] here. A
    // System.Text.Json [JsonPropertyName] only has an effect if you also register a System.Text.Json
    // based CosmosSerializer on the client
    [JsonProperty("id")]
    public string Id { get; set; }

    // ... rest of the saga
}
```

The same rule applies to any of your own documents that Wolverine writes for you through
[`ICosmosDbOp`](#storage-side-effects-icosmosdbop) or the [transactional middleware](#transactional-middleware):
they need a lowercase `id` in the JSON too, whether from camel casing or an explicit mapping.

## Aspire Integration

The cleanest way to integrate Wolverine with .NET Aspire for Cosmos DB is via the `Aspire.Azure.Data.Cosmos` client NuGet, which registers a `CosmosClient` in DI. Wolverine's `UseCosmosDbPersistence()` reads `CosmosClient` from DI automatically.

**AppHost** (`Aspire.Hosting.Azure.CosmosDB` NuGet):
```csharp
var cosmos = builder.AddAzureCosmosDB("cosmos")
    .AddCosmosDatabase("wolverine");

builder.AddProject<Projects.MyWorker>("worker")
    .WithReference(cosmos)
    .WaitFor(cosmos);
```

**Service project** (`Aspire.Azure.Data.Cosmos` NuGet registers `CosmosClient` in DI):
```csharp
using Azure.Identity;

var builder = Host.CreateApplicationBuilder(args);

// Aspire.Azure.Data.Cosmos reads the connection from configuration and
// registers CosmosClient in DI — Wolverine picks it up automatically
builder.AddAzureCosmosClient("cosmos", configureClientOptions: options =>
{
    // Aspire builds the client for you, so this is where saga persistence gets its
    // camel casing. See "Serializer Configuration" above
    options.SerializerOptions = new CosmosSerializationOptions
    {
        PropertyNamingPolicy = CosmosPropertyNamingPolicy.CamelCase
    };
});

builder.UseWolverine(opts =>
{
    opts.UseCosmosDbPersistence("wolverine");
    opts.Policies.AutoApplyTransactions();
});

await builder.Build().RunAsync();
```

For local development with the Cosmos DB emulator, Aspire automatically wires up the emulator endpoint when you call `.RunAsEmulator()` in the AppHost:

```csharp
var cosmos = builder.AddAzureCosmosDB("cosmos")
    .RunAsEmulator()
    .AddCosmosDatabase("wolverine");
```

## Container Setup

Wolverine uses a single CosmosDB container named `wolverine` with a partition key path of `/partitionKey`.
The container is automatically created during database migration if it does not exist.

All Wolverine document types are stored in the same container, differentiated by a `docType` field:
- `incoming` - Incoming message envelopes
- `outgoing` - Outgoing message envelopes
- `deadletter` - Dead letter queue messages
- `node` - Node registration documents
- `agent-assignment` - Agent assignment documents
- `lock` - Distributed lock documents

## Message Persistence

The [durable inbox and outbox](/guide/durability/) options in Wolverine are completely supported with
CosmosDB as the persistence mechanism. This includes scheduled execution (and retries), dead letter queue storage,
and the ability to replay designated messages in the dead letter queue storage.

## Saga Persistence

The CosmosDB integration can serve as a [Wolverine Saga persistence mechanism](/guide/durability/sagas). The only limitation is that
all saga identity values must be `string` types. The saga id is used as both the CosmosDB document id and partition key.

Because it is the document id, the saga's identity member has to serialize as the lowercase `id` CosmosDB
requires — which means the registered `CosmosClient` needs the camel case naming policy described in
[Serializer Configuration](#serializer-configuration). Wolverine enforces this at startup.

### Optimistic Concurrency

Saga writes are protected by optimistic concurrency. Wolverine captures the document's `ETag` when it reads the saga
and passes it back as an `IfMatchEtag` on the subsequent write, so updating (or deleting, when the saga completes)
a saga is a compare-and-swap rather than a blind upsert. If another message changed the same saga in between,
CosmosDB rejects the write with a `412 Precondition Failed` and Wolverine raises a `SagaConcurrencyException`.
Without this, two messages for one saga id arriving at the same time on two nodes would both read the same
revision and the slower write would silently overwrite the faster one.

Because a concurrency violation just means "you read a stale copy, go read it again", the usual response is to
retry the message — the retry re-reads the saga and re-applies the change against the current state:

```cs
opts.Policies.OnException<SagaConcurrencyException>().RetryTimes(3);
```

If you do not configure a policy for it, `SagaConcurrencyException` is treated like any other unhandled exception
and the message will eventually be moved to the dead letter queue.

## Transactional Middleware

Wolverine's CosmosDB integration supports [transactional middleware](/guide/durability/marten/transactional-middleware)
using the CosmosDB `Container` type. When using `AutoApplyTransactions()`, Wolverine will automatically detect
handlers that use `Container` and apply the transactional middleware.

## Storage Side Effects (ICosmosDbOp)

Use `ICosmosDbOp` as return values from handlers for a cleaner approach to CosmosDB operations:

```cs
public static class MyHandler
{
    public static ICosmosDbOp Handle(CreateOrder command)
    {
        var order = new Order { id = command.Id, Name = command.Name };
        return CosmosDbOps.Store(order);
    }
}
```

Available side effect operations:
- `CosmosDbOps.Store<T>(document)` - Upsert a document
- `CosmosDbOps.Delete(id, partitionKey)` - Delete a document by id and partition key

## Outbox Pattern

You can use the `ICosmosDbOutbox` interface to combine CosmosDB operations with outgoing messages
in a single logical transaction:

```cs
public class MyService
{
    private readonly ICosmosDbOutbox _outbox;

    public MyService(ICosmosDbOutbox outbox)
    {
        _outbox = outbox;
    }

    public async Task DoWorkAsync(Container container)
    {
        _outbox.Enroll(container);

        // Send messages through the outbox
        await _outbox.SendAsync(new MyMessage());

        // Flush outgoing messages
        await _outbox.SaveChangesAsync();
    }
}
```

## Dead Letter Queue Management

Dead letter messages are stored in the same CosmosDB container with `docType = "deadletter"` and can be
managed through the standard Wolverine dead letter queue APIs. Messages can be marked as replayable and
will be moved back to the incoming queue.

## Distributed Locking

The CosmosDB integration implements distributed locking using document-based locks with ETag-based
optimistic concurrency. Lock documents have a 5-minute expiration time and are automatically
reclaimed if a node fails to renew them.
