using Wolverine.Http.FluentValidation.Internals;

namespace Wolverine.Http.FluentValidation;

public static class WolverineHttpOptionsExtensions
{
    #region sample_usage_of_http_add_policy
    /// <summary>
    ///     Apply Fluent Validation middleware to all Wolverine HTTP endpoints with a known Fluent Validation
    ///     validator for the request type. From Wolverine 7.0 this is the default whenever
    ///     <c>UseFluentValidation()</c> is called, so calling it is only needed after <c>RestoreV6Defaults()</c>.
    ///     Calling it more than once applies the middleware once.
    /// </summary>
    /// <param name="httpOptions"></param>
    public static void UseFluentValidationProblemDetailMiddleware(this WolverineHttpOptions httpOptions)
    {
        if (httpOptions.Policies.OfType<HttpChainFluentValidationPolicy>().Any()) return;

        httpOptions.AddPolicy<HttpChainFluentValidationPolicy>();
    }

    #endregion

    /// <summary>
    ///     GH-5520. Do not apply the Fluent Validation ProblemDetails middleware to Wolverine HTTP endpoints by
    ///     default, as Wolverine 7.0 does when <c>UseFluentValidation()</c> is called. An explicit
    ///     <see cref="UseFluentValidationProblemDetailMiddleware" /> still applies it.
    /// </summary>
    /// <param name="httpOptions"></param>
    public static void DoNotUseFluentValidationProblemDetailMiddleware(this WolverineHttpOptions httpOptions)
    {
        if (httpOptions.Policies.OfType<FluentValidationProblemDetailMiddlewareOptOut>().Any()) return;

        httpOptions.Policies.Add(new FluentValidationProblemDetailMiddlewareOptOut());
    }
}
