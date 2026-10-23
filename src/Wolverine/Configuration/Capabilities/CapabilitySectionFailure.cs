namespace Wolverine.Configuration.Capabilities;

/// <summary>
///     GH-5162: records one part of the <see cref="ServiceCapabilities" /> snapshot that could not be read, so a
///     consumer can tell "this service has none of these" from "the read failed" and ask again later.
/// </summary>
public class CapabilitySectionFailure
{
    /// <summary>
    ///     The <see cref="ServiceCapabilities" /> section that failed, named after its property, e.g.
    ///     <c>EventStores</c> or <c>DocumentStores</c>
    /// </summary>
    public string Section { get; set; } = null!;

    /// <summary>
    ///     The individual item inside the section that failed, when known -- usually a store's subject URI.
    ///     Null when the whole section failed before any single item could be blamed.
    /// </summary>
    public string? Subject { get; set; }

    /// <summary>
    ///     Full name of the exception type that was thrown
    /// </summary>
    public string ExceptionType { get; set; } = null!;

    public override string ToString()
    {
        return Subject == null ? $"{Section}: {ExceptionType}" : $"{Section} ({Subject}): {ExceptionType}";
    }
}
