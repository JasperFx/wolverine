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

    /// <summary>
    /// GH-5553. Where the receiving service sends the <see cref="Fault{T}"/> of a message that failed, before
    /// falling back to routing. Set by <see cref="DeliveryOptions.FaultTo"/> and
    /// <see cref="DeliveryOptions.ReturnFaultToSender"/>, and stamped automatically on every <c>T</c> a saga
    /// that declares <c>Handle(Fault&lt;T&gt;)</c> or a <c>Compensate</c> for <c>T</c> sends, unless that
    /// application already routes <c>Fault&lt;T&gt;</c> to itself. A receiver that predates the header ignores it.
    /// </summary>
    public const string ReplyTo = "wolverine.fault.reply-to";

    /// <summary>
    /// GH-5553. The value of <see cref="ReplyTo"/> that means "the sender's reply address": the receiver sends the
    /// fault to the failed envelope's <see cref="Envelope.ReplyUri"/>. Written when the sender's reply address is not
    /// known yet as the message is sent.
    /// </summary>
    public const string ReplyToSender = "sender";
}
