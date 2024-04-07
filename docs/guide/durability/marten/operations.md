# Marten Operation Side Effects

::: tip
You can certainly write your own `IMartenOp` implementations and use them as return values in your Wolverine
handlers
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/Wolverine.Marten/IMartenOp.cs#L7-L17' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_imartenop' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Marten/Documents.cs#L42-L51' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_marten_op_from_http_endpoint' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/TodoWebService/TodoWebService/TodoListEndpoint.cs#L18-L35' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_start_stream_side_effect' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The major advantage of using a Marten side effect is to help keep your Wolverine handlers or HTTP endpoints 
be a pure function that can be easily unit tested through measuring the expected return values. Using `IMartenOp` also
helps you utilize synchronous methods for your logic, even though at runtime Wolverine itself will be wrapping asynchronous
code about your simpler, synchronous code.



