using JasperFx.Core;
using Wolverine.Http;
using Wolverine.Marten;

namespace TodoWebService;

public record CreateTodoListRequest(string Title);

public class TodoList;

public record TodoListCreated(Guid ListId, string Title);

public record TodoCreationResponse(Guid ListId) : CreationResponse("api/todo-lists/" + ListId);

#region sample_using_start_stream_side_effect

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

#endregion

