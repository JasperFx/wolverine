namespace Wolverine.Attributes;

/// <summary>
/// GH-4180 follow up. Declares which part of a message type carries its <b>logical</b> deduplication
/// identity, so that <see cref="Envelope.DeduplicationId" /> is derived from the message itself
/// instead of every publisher having to remember to set <c>DeliveryOptions.DeduplicationId</c>.
///
/// <para>
/// Put it on the member that holds the identity:
/// </para>
/// <example>
/// <code>
/// public record RebuildProjection([property: DeduplicationIdentity] string OccurrenceKey, string ProjectionName);
/// </code>
/// </example>
///
/// <para>
/// or on the message type naming the member, which is the option that works for a contract you do
/// not own the members of:
/// </para>
/// <example>
/// <code>
/// [DeduplicationIdentity(nameof(OccurrenceKey))]
/// public record RebuildProjection(string OccurrenceKey, string ProjectionName);
/// </code>
/// </example>
///
/// <para>
/// The member value is stamped onto the outgoing envelope through <see cref="IEnvelopeRule" />, and is
/// only used when nothing has already set an id — an explicit <c>DeliveryOptions.DeduplicationId</c>
/// always wins. It never <i>enforces</i> anything on its own: enforcement is
/// <c>[Deduplicated]</c> on the receiving handler plus
/// <c>opts.Durability.EnableMessageDeduplication</c>.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Class |
                AttributeTargets.Struct | AttributeTargets.Interface)]
public class DeduplicationIdentityAttribute : Attribute
{
    public DeduplicationIdentityAttribute()
    {
    }

    /// <param name="memberName">
    /// The name of the property or field on the message type holding the logical identity. Only
    /// meaningful when this attribute is placed on the message type itself.
    /// </param>
    public DeduplicationIdentityAttribute(string memberName)
    {
        MemberName = memberName;
    }

    /// <summary>
    /// The name of the property or field on the message type holding the logical identity, when this
    /// attribute is placed on the message type rather than directly on the member.
    /// </summary>
    public string? MemberName { get; }
}
