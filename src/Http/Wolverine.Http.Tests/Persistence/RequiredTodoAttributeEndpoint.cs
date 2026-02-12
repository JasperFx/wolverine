using System.ComponentModel.DataAnnotations;
using Alba;
using IntegrationTests;
using Marten;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Shouldly;
using Wolverine.Marten;
using Wolverine.Persistence;
using WolverineWebApi.Todos;
using Xunit.Abstractions;

namespace Wolverine.Http.Tests.Persistence;

public class RequiredTodoAttributeEndpoint {

    public static async Task<(Todo2?, ProblemDetails)> LoadAsync(string id, IDocumentSession session)
    {
        var todo = await session.LoadAsync<Todo2>(id);
        return (todo, todo != null ? WolverineContinue.NoProblems : new ProblemDetails { Detail = "Todo not found by id", Status = StatusCodes.Status404NotFound } );
    }
    // Should 404 w/ ProblemDetails on missing
    [WolverineGet("/required/todo404required/{id}")]
    public static Todo2 GetWithAttribute([Required] Todo2 todo) 
        => todo;
}