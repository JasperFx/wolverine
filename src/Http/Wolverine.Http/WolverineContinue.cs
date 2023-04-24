using Microsoft.AspNetCore.Http;

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
}