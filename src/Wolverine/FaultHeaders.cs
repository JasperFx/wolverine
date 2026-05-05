namespace Wolverine;

/// <summary>
/// Header constants attached by Wolverine when auto-publishing <see cref="Fault{T}"/> events.
/// </summary>
public static class FaultHeaders
{
    /// <summary>
    /// Set to "true" on every Fault&lt;T&gt; envelope that was auto-published by Wolverine's
    /// failure pipeline (as opposed to manually published Fault&lt;T&gt; instances).
    /// </summary>
    public const string AutoPublished = "wolverine.fault.auto";
}
