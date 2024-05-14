using Microsoft.AspNetCore.Mvc;
using Wolverine.Attributes;
using Wolverine.Http;
using Wolverine.Http.Marten;
using Wolverine.Marten;

namespace WolverineWebApi.Bugs;

public class SomeAggregate
{
    Guid Id { get; set; }
}

public class SomeCommand;

public static class SomeMiddleware
{
    public static async Task<ProblemDetails> BeforeAsync(SomeAggregate theAggregate)
    {
        return WolverineContinue.NoProblems;
    }
}

public static class AggregateAndMiddlwareEndpoint
{
    [WolverinePost("/aggregate/{id}")]
    [Middleware(typeof(SomeMiddleware))]
    public static async Task<Events> Handle(SomeCommand cmd, [Aggregate("id")]SomeAggregate theAggregate)
    {
        return [];
    }
}