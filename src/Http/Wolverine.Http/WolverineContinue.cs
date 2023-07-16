using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Wolverine.Http;

/// <summary>
///     Used with Wolverine HTTP middleware to say "keep going, nothing to see here"
///     when returned from Middleware methods
/// </summary>
public struct WolverineContinue : IResult
{
    Task IResult.ExecuteAsync(HttpContext httpContext)
    {
        return Task.CompletedTask;
    }

    public WolverineContinue()
    {
    }

    public static WolverineContinue Result()
    {
        return new WolverineContinue();
    }

    /// <summary>
    /// When Wolverine sees this value, it will interpret the continuation as
    /// everything is fine, full steam ahead
    /// </summary>
    public static ProblemDetails NoProblems { get; } = new();
}