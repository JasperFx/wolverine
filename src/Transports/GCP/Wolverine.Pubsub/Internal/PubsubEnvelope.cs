namespace Wolverine.Pubsub.Internal;

/// <summary>
/// An envelope read from a single (not batched) Pub/Sub message.
/// </summary>
internal class PubsubEnvelope : Envelope
{
    /// <summary>
    /// GH-5771. The native <c>OrderingKey</c> the message was received with. A publisher other than Wolverine sets
    /// only that, never Wolverine's <c>group-id</c> attribute, so <see cref="Envelope.GroupId" /> stays empty and a
    /// requeue copy would otherwise be republished without any ordering key.
    /// </summary>
    public string? ReceivedOrderingKey { get; init; }
}
