using JasperFx.Core;
using Wolverine.Http;
using Wolverine.Marten;

namespace NSwagDemonstrator;

public record CreateTodoListRequest(string Title);

public class TodoList;

public record TodoListCreated(Guid ListId, string Title);

public static class TodoListEndpoint
{
    [WolverinePost("/api/todo-lists")]
    public static (IResult, IStartStream) CreateTodoList(
        CreateTodoListRequest request,
        HttpRequest req
    )
    {
        var listId = CombGuidIdGeneration.NewGuid();
        var result = new TodoListCreated(listId, request.Title);
        var startStream = MartenOps.StartStream<TodoList>(result);
        var response = Results.Created("api/todo-lists/" + listId, result);

        return (response, startStream);
    }
}