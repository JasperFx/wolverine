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

There are existing Polecat ops for storing, inserting, updating, and deleting a document. There's also a specific
helper for starting a new event stream as shown below:

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
