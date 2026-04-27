# Polecat Operation Side Effects

::: tip
You can certainly write your own `IPolecatOp` implementations and use them as return values in your Wolverine
handlers
:::

::: info
This integration also includes full support for the [storage action side effects](/guide/handlers/side-effects.html#storage-side-effects)
model when using Polecat with Wolverine.
:::

The `Wolverine.Polecat` library includes some helpers for Wolverine [side effects](/guide/handlers/side-effects) using
Polecat with the `IPolecatOp` interface:

```cs
/// <summary>
/// Interface for any kind of Polecat related side effect
/// </summary>
public interface IPolecatOp : ISideEffect
{
    void Execute(IDocumentSession session);
}
```

The built in side effects can all be used from the `PolecatOps` static class like this HTTP endpoint example:

```cs
[WolverinePost("/invoices/{invoiceId}/pay")]
public static IPolecatOp Pay([Entity] Invoice invoice)
{
    invoice.Paid = true;
    return PolecatOps.Store(invoice);
}
```

There are existing Polecat ops for storing, inserting, updating, and deleting a document.

### Storing Multiple Documents

Use `PolecatOps.StoreMany()` to store multiple documents of the same type, or `PolecatOps.StoreObjects()` to
store multiple documents of different types in a single side effect:

```csharp
// Store multiple documents of the same type
public static StoreManyDocs<Invoice> Handle(BatchInvoiceCommand command)
{
    var invoices = command.Items.Select(i => new Invoice { Id = i.Id, Amount = i.Amount });
    return PolecatOps.StoreMany(invoices.ToArray());
}

// Store multiple documents of different types
public static StoreObjects Handle(CreateOrderCommand command)
{
    var order = new Order { Id = command.OrderId, Total = command.Total };
    var audit = new AuditLog { Action = "OrderCreated", EntityId = command.OrderId };
    return PolecatOps.StoreObjects(order, audit);
}
```

Both `StoreMany()` and `StoreObjects()` support fluent `With()` methods to incrementally add documents:

```csharp
public static StoreObjects Handle(ComplexCommand command)
{
    return PolecatOps.StoreObjects(new Order { Id = command.OrderId })
        .With(new AuditLog { Action = "Created" })
        .With(new Notification { Message = "Order created" });
}
```

### Tenant-Scoped Operations

Every `PolecatOps` factory method has an overload that accepts a `tenantId` parameter. When provided, the
operation uses `IDocumentSession.ForTenant(tenantId)` to scope the write to a specific tenant. This is
useful in multi-tenant systems where a handler processing a message for one tenant needs to write data
to a different tenant's storage:

```csharp
// Store a document in a specific tenant
public static StoreDoc<Invoice> Handle(CreateInvoiceForTenant command)
{
    var invoice = new Invoice { Id = command.InvoiceId, Amount = command.Amount };
    return PolecatOps.Store(invoice, command.TenantId);
}

// Store many same-type documents in a specific tenant
public static StoreManyDocs<LineItem> Handle(BatchLineItems command)
{
    return PolecatOps.StoreMany(command.TenantId, command.Items.ToArray());
}

// Store mixed-type documents in a specific tenant
public static StoreObjects Handle(CrossTenantAudit command)
{
    return PolecatOps.StoreObjects(command.TargetTenantId,
        new AuditRecord { Action = command.Action },
        new Notification { Message = command.Message });
}
```

All existing method signatures are unchanged — the tenant overloads are purely additive.

There's also a specific helper for starting a new event stream as shown below:

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
        var startStream = PolecatOps.StartStream<TodoList>(listId, result);

        return (new TodoCreationResponse(listId), startStream);
    }
}
```

The major advantage of using a Polecat side effect is to help keep your Wolverine handlers or HTTP endpoints
be a pure function that can be easily unit tested through measuring the expected return values. Using `IPolecatOp` also
helps you utilize synchronous methods for your logic, even though at runtime Wolverine itself will be wrapping asynchronous
code about your simpler, synchronous code.

## Returning Multiple Polecat Side Effects

Wolverine lets you return zero to many `IPolecatOp` operations as side effects
from a message handler or HTTP endpoint method like so:

```cs
public static IEnumerable<IPolecatOp> Handle(AppendManyNamedDocuments command)
{
    var number = 1;
    foreach (var name in command.Names)
    {
        yield return PolecatOps.Store(new NamedDocument{Id = name, Number = number++});
    }
}
```

Wolverine will pick up on any return type that can be cast to `IEnumerable<IPolecatOp>`, so for example:

* `IEnumerable<IPolecatOp>`
* `IPolecatOp[]`
* `List<IPolecatOp>`
