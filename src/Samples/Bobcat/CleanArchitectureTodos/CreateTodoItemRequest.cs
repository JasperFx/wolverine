using FluentValidation;
using Marten;
using Microsoft.AspNetCore.Http.HttpResults;
using Wolverine.Http;
using Wolverine.Persistence;

namespace CleanArchitectureTodos;

public record CreateTodoItemRequest(int ListId, string Title)
{
    public class Validator : AbstractValidator<CreateTodoItemRequest>
    {
        public Validator()
        {
            RuleFor(x => x.Title).NotEmpty().MaximumLength(200);
            RuleFor(x => x.ListId).GreaterThan(0);
        }
    }
}

public static class CreateTodoItemEndpoint
{
    // [Entity] loads the TodoList by the "ListId" property on the request.
    //
    // Created rather than a bare TodoItem, so the response carries the 201 a newly-created
    // resource should. Returned as a TypedResults value directly rather than as a
    // (TodoItem, IResult) tuple — see docs/sample-wiring.md footgun 3. Items are nested in
    // their list's document, so the Location points at the list that now contains it.
    [WolverinePost("/api/todoitems")]
    public static Created<TodoItem> Post(
        CreateTodoItemRequest request,
        [Entity("ListId", Required = true)] TodoList list,
        IDocumentSession session)
    {
        var item = new TodoItem { Title = request.Title };
        list.Items.Add(item);
        list.LastModified = DateTimeOffset.UtcNow;

        session.Store(list);

        return TypedResults.Created($"/api/todolists/{list.Id}", item);
    }
}
