# Marten Operation Side Effects

::: tip
You can certainly write your own `IMartenOp` implementations and use them as return values in your Wolverine
handlers
:::

::: info
This integration also includes full support for the [storage action side effects](/guide/handlers/side-effects.html#storage-side-effects)
model when using Marten~~~~ with Wolverine.
:::

The `Wolverine.Marten` library includes some helpers for Wolverine [side effects](/guide/handlers/side-effects) using
Marten with the `IMartenOp` interface:

<!-- snippet: sample_imartenop -->
<a id='snippet-sample_imartenop'></a>
```cs
/// <summary>
/// Interface for any kind of Marten related side effect
/// </summary>
public interface IMartenOp : ISideEffect
{
    void Execute(IDocumentSession session);
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/Wolverine.Marten/IMartenOp.cs#L19-L28' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_imartenop' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The built in side effects can all be used from the `MartenOps` static class like this HTTP endpoint example:

<!-- snippet: sample_using_marten_op_from_http_endpoint -->
<a id='snippet-sample_using_marten_op_from_http_endpoint'></a>
```cs
[WolverinePost("/invoices/{invoiceId}/pay")]
public static IMartenOp Pay([Document] Invoice invoice)
{
    invoice.Paid = true;
    return MartenOps.Store(invoice);
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Marten/Documents.cs#L42-L50' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_marten_op_from_http_endpoint' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

There are existing Marten ops for storing, inserting, updating, and deleting a document.

### Storing Multiple Documents

Use `MartenOps.StoreMany()` to store multiple documents of the same type, or `MartenOps.StoreObjects()` to store
multiple documents of different types in a single side effect:

```csharp
// Store multiple documents of the same type
public static StoreManyDocs<Invoice> Handle(BatchInvoiceCommand command)
{
    var invoices = command.Items.Select(i => new Invoice { Id = i.Id, Amount = i.Amount });
    return MartenOps.StoreMany(invoices.ToArray());
}

// Store multiple documents of different types
public static StoreObjects Handle(CreateOrderCommand command)
{
    var order = new Order { Id = command.OrderId, Total = command.Total };
    var audit = new AuditLog { Action = "OrderCreated", EntityId = command.OrderId };
    return MartenOps.StoreObjects(order, audit);
}
```

Both `StoreMany()` and `StoreObjects()` support fluent `With()` methods to incrementally add documents:

```csharp
public static StoreObjects Handle(ComplexCommand command)
{
    return MartenOps.StoreObjects(new Order { Id = command.OrderId })
        .With(new AuditLog { Action = "Created" })
        .With(new Notification { Message = "Order created" });
}
```

### Tenant-Scoped Operations

Every `MartenOps` factory method has an overload that accepts a `tenantId` parameter. When provided, the
operation uses `IDocumentSession.ForTenant(tenantId)` to scope the write to a specific tenant. This is
useful in multi-tenant systems where a handler processing a message for one tenant needs to write data
to a different tenant's storage:

```csharp
// Store a document in a specific tenant
public static StoreDoc<Invoice> Handle(CreateInvoiceForTenant command)
{
    var invoice = new Invoice { Id = command.InvoiceId, Amount = command.Amount };
    return MartenOps.Store(invoice, command.TenantId);
}

// Insert a document in a specific tenant
public static InsertDoc<AuditRecord> Handle(CrossTenantAudit command)
{
    var record = new AuditRecord { Action = command.Action };
    return MartenOps.Insert(record, command.TargetTenantId);
}

// Delete by id in a specific tenant
public static DeleteDocById<Invoice> Handle(CancelInvoice command)
{
    return MartenOps.Delete<Invoice>(command.InvoiceId, command.TenantId);
}

// Store many documents in a specific tenant
public static StoreManyDocs<LineItem> Handle(BatchLineItems command)
{
    return MartenOps.StoreMany(command.TenantId, command.Items.ToArray());
}

// Delete matching documents in a specific tenant
public static DeleteDocWhere<TempRecord> Handle(CleanupTenant command)
{
    return MartenOps.DeleteWhere<TempRecord>(
        x => x.CreatedAt < DateTimeOffset.UtcNow.AddDays(-30),
        command.TenantId
    );
}
```

All existing method signatures are unchanged — the tenant overloads are purely additive.

### Scoping Any Operation to a Tenant <Badge type="tip" text="6.35" />

Rather than growing a parallel `tenantId` overload for every factory method, any `MartenOps`
operation can be pointed at a tenant with `ForTenant()`:

```csharp
public static IMartenOp Handle(ArchiveTenantOrder command)
{
    return MartenOps.ArchiveStream(command.OrderId).ForTenant(command.TenantId);
}
```

`ForTenant()` returns the same operation with its concrete type intact, so it composes with the
fluent `With()` methods and can still be returned as a specific type. It works on every built-in
operation and on your own `IMartenOp` implementations as soon as they implement
`ITenantedMartenOp`.

There's also a specific helper for starting a new event stream as shown below:

<!-- snippet: sample_using_start_stream_side_effect -->
<a id='snippet-sample_using_start_stream_side_effect'></a>
```cs
public static class TodoListEndpoint
{
    [WolverinePost("/api/todo-lists")]
    public static (TodoCreationResponse, IStartStream) CreateTodoList(
        CreateTodoListRequest request
    )
    {
        var listId = CombGuidIdGeneration.NewGuid();
        var result = new TodoListCreated(listId, request.Title);
        var startStream = MartenOps.StartStream<TodoList>(listId, result);

        return (new TodoCreationResponse(listId), startStream);
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/TodoWebService/TodoWebService/TodoListEndpoint.cs#L15-L31' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_start_stream_side_effect' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The major advantage of using a Marten side effect is to help keep your Wolverine handlers or HTTP endpoints 
be a pure function that can be easily unit tested through measuring the expected return values. Using `IMartenOp` also
helps you utilize synchronous methods for your logic, even though at runtime Wolverine itself will be wrapping asynchronous
code about your simpler, synchronous code.

## Returning Multiple Marten Side Effects <Badge type="tip" text="3.6" />

Due to (somewhat) popular demand, Wolverine lets you return zero to many `IMartenOp` operations as side effects
from a message handler or HTTP endpoint method like so:

<!-- snippet: sample_using_ienumerable_of_martenop_as_side_effect -->
<a id='snippet-sample_using_ienumerable_of_martenop_as_side_effect'></a>
```cs
// Just keep in mind that this "example" was rigged up for test coverage
public static IEnumerable<IMartenOp> Handle(AppendManyNamedDocuments command)
{
    var number = 1;
    foreach (var name in command.Names)
    {
        yield return MartenOps.Store(new NamedDocument{Id = name, Number = number++});
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/MartenTests/handler_actions_with_implied_marten_operations.cs#L349-L360' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_ienumerable_of_martenop_as_side_effect' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Wolverine will pick up on any return type that can be cast to `IEnumerable<IMartenOp>`, so for example:

* `IEnumerable<IMartenOp>`
* `IMartenOp[]`
* `List<IMartenOp>`

And you get the point. Wolverine is not (yet) smart enough to know that an array or enumerable of a concrete
type of `IMartenOp` is a side effect.

Like any other "side effect", you could technically return this as the main return type of a method or as part of a
tuple.

## Hard Deletes and Undoing Soft Deletes <Badge type="tip" text="6.35" />

For a [soft-deleted](https://martendb.io/documents/deletes.html#soft-deletes) document type,
`MartenOps.Delete()` only marks the document as deleted. Use `MartenOps.HardDelete()` to remove
the underlying database row, and `MartenOps.UndoDeleteWhere()` to reverse a soft deletion:

```csharp
// Hard delete by document
public static IMartenOp Handle(PurgeInvoice command, Invoice invoice)
{
    return MartenOps.HardDelete(invoice);
}

// Hard delete by id - string, Guid, int, and long ids are all supported
public static IMartenOp Handle(PurgeInvoiceById command)
{
    return MartenOps.HardDelete<Invoice>(command.InvoiceId);
}

// Hard delete everything matching a filter
public static IMartenOp Handle(PurgeOldInvoices command)
{
    return MartenOps.HardDeleteWhere<Invoice>(x => x.CreatedAt < command.Cutoff);
}

// Bring soft-deleted documents back
public static IMartenOp Handle(RestoreInvoices command)
{
    return MartenOps.UndoDeleteWhere<Invoice>(x => x.CustomerId == command.CustomerId);
}
```

::: warning
Marten's `HardDelete<T>()` only accepts `string`, `Guid`, `int`, and `long` identities — it has no
`object` overload to fall back on the way `Delete<T>()` does. Passing anything else (a strongly
typed id, say) throws an `ArgumentOutOfRangeException` from the `MartenOps` factory rather than
failing later inside `SaveChangesAsync()`.
:::

## Mixed Document Batches <Badge type="tip" text="6.35" />

`MartenOps.InsertObjects()` and `MartenOps.DeleteObjects()` are the insert and delete counterparts
of `StoreObjects()`, and support the same fluent `With()` methods:

```csharp
public static IMartenOp Handle(CreateOrder command)
{
    return MartenOps.InsertObjects(new Order { Id = command.OrderId })
        .With(new AuditLog { Action = "Created" });
}
```

Where `StoreObjects()` upserts, `InsertObjects()` will fail the transaction if any of the documents
already exist.

## Revision-Checked Updates <Badge type="tip" text="6.35" />

For document types using Marten's [numeric revisioning](https://martendb.io/documents/concurrency.html),
these operations carry the expected revision into the update:

```csharp
// Fails with a ConcurrencyException if the stored revision is >= the supplied one
public static IMartenOp Handle(ReviseInvoice command, Invoice invoice)
{
    return MartenOps.UpdateRevision(invoice, command.Revision);
}

// Same check, but silently does nothing instead of throwing
public static IMartenOp Handle(MaybeReviseInvoice command, Invoice invoice)
{
    return MartenOps.TryUpdateRevision(invoice, command.Revision);
}

// The Guid-versioned equivalent for types using Marten's optimistic concurrency
public static IMartenOp Handle(UpdateInvoice command, Invoice invoice)
{
    return MartenOps.UpdateExpectedVersion(invoice, command.Version);
}
```

## Patching <Badge type="tip" text="6.35" />

`MartenOps.Patch()` and `MartenOps.PatchWhere()` wrap Marten's
[patching API](https://martendb.io/documents/patching.html), so a handler can modify a stored
document without loading it first. The lambda is applied to Marten's fluent patch expression when
the side effect executes:

```csharp
// Patch a single document by id
public static IMartenOp Handle(MarkInvoicePaid command)
{
    return MartenOps.Patch<Invoice>(command.InvoiceId, x => x.Set(i => i.Paid, true));
}

// Patch every document matching a filter
public static IMartenOp Handle(ReassignInvoices command)
{
    return MartenOps.PatchWhere<Invoice>(
        x => x.CustomerId == command.OldCustomerId,
        x => x.Set(i => i.CustomerId, command.NewCustomerId));
}
```

As with `HardDelete()`, the by-id overloads accept `string`, `Guid`, `int`, and `long` identities.

## Raw SQL <Badge type="tip" text="6.35" />

`MartenOps.QueueSqlCommand()` enlists a raw SQL statement into the same batched unit of work as the
rest of the handler's Marten operations, so it commits or rolls back with them. Use `?` for
positional parameters, or supply your own placeholder character:

```csharp
public static IMartenOp Handle(RecordInvoiceMetric command)
{
    return MartenOps.QueueSqlCommand(
        "insert into invoice_metrics (invoice_id, amount) values (?, ?)",
        command.InvoiceId, command.Amount);
}
```

## Appending to and Archiving an Existing Stream <Badge type="tip" text="6.35" />

`MartenOps.StartStream()` covers new streams. To append to a stream that already exists, or to
archive one, use `MartenOps.Append()` and `MartenOps.ArchiveStream()`:

```csharp
// Append to a stream, by Guid id or string key
public static IMartenOp Handle(RecordShipment command)
{
    return MartenOps.Append(command.OrderId, new OrderShipped(command.ShippedAt));
}

// Append with an optimistic concurrency check on the stream version
public static IMartenOp Handle(RecordShipmentAtVersion command)
{
    return MartenOps.Append(command.OrderId, command.ExpectedVersion,
        new OrderShipped(command.ShippedAt));
}

// Archive a completed stream
public static IMartenOp Handle(CloseOrder command)
{
    return MartenOps.ArchiveStream(command.OrderId);
}
```

::: tip
If the handler is working on an aggregate of its own, prefer the
[aggregate handler workflow](/guide/durability/marten/event-sourcing) — `[WriteAggregate]` and the
`Events` return type give you the aggregate state and its concurrency protection as well. These
side effects are for the other case: touching some *other* stream from a handler that has no
aggregate of its own.
:::

## Data Requirements <Badge type="tip" text="5.13" />

Wolverine provides declarative data requirement checks that verify whether a Marten document exists (or does not
exist) before a handler or HTTP endpoint executes. If the check fails, a `RequiredDataMissingException` is thrown,
preventing the handler from running.

### Using Attributes

The simplest way to declare data requirements is with the `[DocumentExists<T>]` and `[DocumentDoesNotExist<T>]`
attributes on handler methods:

```csharp
// Convention: looks for a property named "UserId" or "Id" on the command
[DocumentExists<User>]
public void Handle(PromoteUser command)
{
    // Only runs if a User document with the matching identity exists
}

// Explicit property name for the identity
[DocumentDoesNotExist<User>(nameof(AddUser.UserId))]
public void Handle(AddUser command)
{
    // Only runs if no User document with the matching identity exists
}
```

The identity property is resolved from the message/request type by convention:
1. If a property name is specified explicitly in the attribute constructor, that is used
2. Otherwise, Wolverine looks for a property named `{DocumentTypeName}Id` (e.g., `UserId` for `User`)
3. As a fallback, Wolverine looks for a property named `Id`

You can apply multiple attributes to a single handler method to check multiple documents:

```csharp
[DocumentExists<Department>(nameof(TransferEmployee.TargetDepartmentId))]
[DocumentExists<Employee>]
public void Handle(TransferEmployee command)
{
    // Only runs if both the employee and target department exist
}
```

### Using the Before Method Pattern

For more complex requirements, or when you need access to the command properties at runtime to construct
the check, use the `Before` method pattern with `MartenOps.Document<T>()`:

```csharp
public static class CreateThingHandler
{
    // Single requirement
    public static IMartenDataRequirement Before(CreateThing command)
        => MartenOps.Document<ThingCategory>().MustExist(command.Category);

    public static IMartenOp Handle(CreateThing command)
    {
        return MartenOps.Store(new Thing
        {
            Id = command.Name,
            CategoryId = command.Category
        });
    }
}

public static class CreateThing2Handler
{
    // Multiple requirements
    public static IEnumerable<IMartenDataRequirement> Before(CreateThing2 command)
    {
        yield return MartenOps.Document<ThingCategory>().MustExist(command.Category);
        yield return MartenOps.Document<Thing>().MustNotExist(command.Name);
    }

    public static IMartenOp Handle(CreateThing2 command)
    {
        return MartenOps.Store(new Thing
        {
            Id = command.Name,
            CategoryId = command.Category
        });
    }
}
```

When multiple data requirements are present in the same handler (whether from attributes or `Before` methods),
Wolverine will automatically batch the existence checks into a single Marten batch query for efficiency.



