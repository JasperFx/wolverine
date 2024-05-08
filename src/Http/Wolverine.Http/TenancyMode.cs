namespace Wolverine.Http;

public enum TenancyMode
{
    /// <summary>
    /// This endpoint functions with or without a detected tenant id
    /// </summary>
    Maybe,

    /// <summary>
    /// This endpoint does not use multi-tenancy at all
    /// </summary>
    None,

    /// <summary>
    /// This endpoint requires a non null tenant id
    /// </summary>
    Required
}