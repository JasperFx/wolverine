using Wolverine.Http.FluentValidation.Internals;

namespace Wolverine.Http.FluentValidation;

public static class WolverineHttpOptionsExtensions
{
    /// <summary>
    ///     Apply Fluent Validation middleware to all Wolverine HTTP endpoints with a known Fluent Validation
    ///     validator for the request type
    /// </summary>
    /// <param name="httpOptions"></param>
    public static void UseFluentValidationProblemDetailMiddleware(this WolverineHttpOptions httpOptions)
    {
        httpOptions.AddPolicy<HttpChainFluentValidationPolicy>();
    }
}