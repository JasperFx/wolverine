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

There are existing Marten ops for storing, inserting, updating, and deleting a document. There's also a specific
helper for starting a new event stream as shown below:

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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/MartenTests/handler_actions_with_implied_marten_operations.cs#L330-L342' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_ienumerable_of_martenop_as_side_effect' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Wolverine will pick up on any return type that can be cast to `IEnumerable<IMartenOp>`, so for example:

* `IEnumerable<IMartenOp>`
* `IMartenOp[]`
* `List<IMartenOp>`

And you get the point. Wolverine is not (yet) smart enough to know that an array or enumerable of a concrete
type of `IMartenOp` is a side effect.

Like any other "side effect", you could technically return this as the main return type of a method or as part of a
tuple.



