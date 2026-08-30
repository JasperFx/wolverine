namespace Wolverine.Runtime.Deduplication;

/// <summary>
/// An <see cref="IEnvelopeRule" /> that derives <see cref="Envelope.DeduplicationId" /> from the
/// outgoing message. <see cref="Matches" /> is asked once per message type when the route is built,
/// so a rule that cannot apply to a message type costs nothing per message.
/// </summary>
internal interface IDeduplicationIdRule : IEnvelopeRule
{
    /// <summary>
    /// Can this rule derive an id for messages of this type? Evaluated at routing-compile time.
    /// </summary>
    bool Matches(Type messageType);
}
