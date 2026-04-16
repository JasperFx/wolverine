namespace Wolverine.Http.ContentNegotiation;

/// <summary>
/// Controls how content negotiation behaves when no matching content type writer is found
/// </summary>
public enum ConnegMode
{
    /// <summary>
    /// Default. Falls back to JSON serialization when no specific writer matches the Accept header.
    /// This is the most permissive mode and ensures clients always get a response.
    /// </summary>
    Loose,

    /// <summary>
    /// Returns HTTP 406 Not Acceptable when no specific writer matches the Accept header.
    /// Use this mode to strictly enforce content negotiation.
    /// </summary>
    Strict
}
