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
    new CosmosClientOptions { /* options */ }
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
