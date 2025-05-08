using System.Diagnostics;
using Wolverine;
using Wolverine.Http;
using Wolverine.Persistence;
using WolverineWebApi.Todos;

namespace WolverineWebApi.Marten;

public static class TodoEndpoint
{
    [WolverinePost("/api/todo2/{id}/something")]
    public static async Task<IResult> Handle([Entity("Id")] Todo2 todo, IMessageBus bus)
    {
        if (todo.IsComplete)
        {
            await bus.PublishAsync(new DoSomethingWithTodo(todo.Id));
            return Results.Accepted();
        }

        return Results.BadRequest("Some reason or other");
    }
}

public record DoSomethingWithTodo(string TodoId);

public static class DoSomethingWithTodoHandler
{
    public static void Handle(DoSomethingWithTodo _) => Debug.WriteLine("Got a DoSomethingWithTodo message");
}

