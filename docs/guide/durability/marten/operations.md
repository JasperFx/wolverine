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

<!-- snippet: sample_IMartenOp -->
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/Wolverine.Marten/IMartenOp.cs#L18-L28' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_imartenop' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Marten/Documents.cs#L43-L52' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_marten_op_from_http_endpoint' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/TodoWebService/TodoWebService/TodoListEndpoint.cs#L15-L32' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_start_stream_side_effect' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/MartenTests/handler_actions_with_implied_marten_operations.cs#L342-L354' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_ienumerable_of_martenop_as_side_effect' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Wolverine will pick up on any return type that can be cast to `IEnumerable<IMartenOp>`, so for example:

* `IEnumerable<IMartenOp>`
* `IMartenOp[]`
* `List<IMartenOp>`

And you get the point. Wolverine is not (yet) smart enough to know that an array or enumerable of a concrete
type of `IMartenOp` is a side effect.

Like any other "side effect", you could technically return this as the main return type of a method or as part of a
tuple.

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



