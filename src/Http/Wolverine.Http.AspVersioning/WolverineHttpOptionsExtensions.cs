namespace Wolverine.Http.AspVersioning;

public static class WolverineHttpOptionsExtensions
{
    /// <summary>
    /// Enable API versioning support via integration with <c>Asp.Versioning.Http</c>. The first call
    /// registers an <c>AspVersioningPolicy</c> that applies versioning semantics at bootstrap time.
    /// Subsequent calls are no-ops.
    /// </summary>
    /// <remarks>
    /// An exception will be thrown during startup if Asp.Versioning is not configured or if Wolverine's
    /// native API versioning (<c>UseApiVersioning()</c>) is also enabled.
    /// </remarks>
    public static void UseAspVersioning(this WolverineHttpOptions httpOptions)
    {
        if (!httpOptions.Policies.Any(policy => policy is AspVersioningPolicy))
            httpOptions.AddPolicy<AspVersioningPolicy>();
    }
}
