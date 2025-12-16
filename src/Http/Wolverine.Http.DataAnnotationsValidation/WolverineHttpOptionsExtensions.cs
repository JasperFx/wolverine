using Wolverine.Http.DataAnnotationsValidation.Internals;

namespace Wolverine.Http.DataAnnotationsValidation;

public static class WolverineHttpOptionsExtensions
{
    #region sample_usage_of_http_add_policy

    /// <summary>
    ///     Apply DataAnnotations Validation middleware to all Wolverine HTTP endpoints
    /// </summary>
    /// <param name="httpOptions"></param>
    public static void UseDataAnnotationsValidationProblemDetailMiddleware(this WolverineHttpOptions httpOptions)
    {
        httpOptions.AddPolicy<HttpChainDataAnnotationsValidationPolicy>();
    }

    #endregion
}