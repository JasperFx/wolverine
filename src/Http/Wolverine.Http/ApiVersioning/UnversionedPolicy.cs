namespace Wolverine.Http.ApiVersioning;

/// <summary>
/// Behaviour applied by <see cref="WolverineApiVersioningOptions"/> when an HTTP endpoint is discovered
/// without an <see cref="Asp.Versioning.ApiVersionAttribute"/>.
/// </summary>
public enum UnversionedPolicy
{
    /// <summary>Endpoint stays at its declared route, no version metadata is attached.</summary>
    PassThrough,

    /// <summary>Bootstrap throws if any chain is missing an <see cref="Asp.Versioning.ApiVersionAttribute"/>.</summary>
    RequireExplicit,

    /// <summary>Endpoint is automatically assigned <see cref="WolverineApiVersioningOptions.DefaultVersion"/>.</summary>
    AssignDefault
}
