using FluentValidation;
using Marten;
using Microsoft.AspNetCore.Mvc;
using Wolverine.Http;
using Wolverine.Persistence;

namespace CleanArchitectureTodos;

public record UpdateTodoListRequest(string Title, string? Colour)
{
    public class Validator : AbstractValidator<UpdateTodoListRequest>
    {
        public Validator()
        {
            RuleFor(x => x.Title).NotEmpty().MaximumLength(200);
        }
    }
}

public static class UpdateTodoListEndpoint
{
    public static async Task<ProblemDetails> ValidateAsync(
        int id,
        UpdateTodoListRequest request,
        IQuerySession session)
    {
        var duplicate = await session.Query<TodoList>().AnyAsync(l => l.Title == request.Title && l.Id != id);
        if (duplicate)
            return new ProblemDetails
            {
                Detail = $"'{request.Title}' already exists.",
                Status = 400,
            };

        return WolverineContinue.NoProblems;
    }

    [WolverinePut("/api/todolists/{id}")]
    public static TodoList Put(
        UpdateTodoListRequest request,
        [Entity(Required = true)] TodoList list,
        IDocumentSession session)
    {
        list.Title = request.Title;
        list.Colour = request.Colour ?? list.Colour;
        list.LastModified = DateTimeOffset.UtcNow;

        session.Store(list);

        return list;
    }
}
