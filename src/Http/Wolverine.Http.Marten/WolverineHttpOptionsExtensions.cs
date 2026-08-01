namespace Wolverine.Http.Marten;

public static class WolverineHttpOptionsExtensions
{
    /// <summary>
    /// Adds an <see cref="IResourceWriterPolicy"/> that streams <see cref="ICompiledQuery"/>
    /// </summary>
    /// <param name="options">Options to apply policy on</param>
    public static void UseMartenCompiledQueryResultPolicy(this WolverineHttpOptions options,
        string responseType = "application/json", int successStatusCode = 200)
    {
        options.AddResourceWriterPolicy(new CompiledQueryWriterPolicy(responseType, successStatusCode));
    }

    /// <summary>
    /// Adds a <see cref="ConcurrencyExceptionPolicy"/> that responds with a ProblemDetails body and the
    /// supplied status code (409 Conflict by default) when a Marten optimistic concurrency exception would
    /// otherwise escape an HTTP endpoint using the aggregate handler workflow or Marten transactional middleware
    /// </summary>
    /// <param name="options">Options to apply policy on</param>
    /// <param name="statusCode">The HTTP status code of the ProblemDetails response. Default is 409 (Conflict)</param>
    public static void UseProblemDetailsForConcurrencyExceptions(this WolverineHttpOptions options,
        int statusCode = 409)
    {
        options.Policies.Add(new ConcurrencyExceptionPolicy(statusCode));
    }
}