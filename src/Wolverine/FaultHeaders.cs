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

    /// <summary>
    /// Set on every auto-published Fault&lt;T&gt; envelope to the original failing envelope's
    /// Id (as a string). Lets trace consumers correlate the Fault envelope with the
    /// envelope that produced it without inspecting the Fault&lt;T&gt; message body.
    /// </summary>
    public const string OriginalId = "wolverine.fault.original_id";

    /// <summary>
    /// Set on every auto-published Fault&lt;T&gt; envelope to the original failing message's
    /// Wolverine message-type name (as produced by <c>ToMessageTypeName()</c>).
    /// </summary>
    public const string OriginalType = "wolverine.fault.original_type";
}
