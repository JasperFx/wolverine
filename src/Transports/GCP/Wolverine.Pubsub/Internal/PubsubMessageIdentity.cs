using System.Security.Cryptography;
using Google.Cloud.PubSub.V1;
using JasperFx.Core;

namespace Wolverine.Pubsub.Internal;

/// <summary>
///     Gives a Pub/Sub message that arrives without a Wolverine id one derived from the message itself, so that every
///     delivery of the same message is the same <see cref="Envelope" /> to the durable inbox.
///     <para>
///         The durable inbox deduplicates on <see cref="Envelope.Id" />. A message Wolverine published carries its id
///         in the <c>id</c> attribute. A message from any other publisher does not, and used to get a fresh id on every
///         delivery, so a redelivery -- an ack deadline that expired, a subscriber that went away before its ack
///         reached Pub/Sub -- was stored again and handled a second time.
///     </para>
///     <para>
///         Pub/Sub gives every message an id that is unique within its topic and unchanged across redeliveries. One
///         message is delivered once to each subscription on the topic, and each subscription must handle it once, so
///         the derived id is keyed on the full subscription resource name, <c>projects/{project}/subscriptions/{id}</c>,
///         as well. The project in that name also tells apart the tenants of a project-per-tenant transport.
///     </para>
///     <para>
///         Pulled and pushed deliveries share this one definition (GH-5016), so the same message delivered to the same
///         subscription has the same id however it arrives.
///     </para>
/// </summary>
internal static class PubsubMessageIdentity
{
    /// <summary>
    ///     Maps <paramref name="message" />, delivered by <paramref name="subscription" />, into a new envelope. When the
    ///     mapping supplies no id of its own -- there is no Wolverine <c>id</c> attribute and a custom mapper set none --
    ///     the envelope gets the id of the Pub/Sub message on that subscription.
    /// </summary>
    internal static Envelope ReadEnvelope(IPubsubEnvelopeMapper mapper, PubsubMessage message, string subscription)
    {
        var envelope = new Envelope();

        // A fresh envelope's id is a new IdGenerator() value nobody else has seen, so the id still being that value
        // after mapping means neither the id attribute nor a custom mapper supplied one
        var provisionalId = envelope.Id;

        mapper.MapIncomingToEnvelope(envelope, message);

        // The default mapping reads a missing or unreadable id attribute as Guid.Empty, which the durable receiver
        // would replace with a fresh id just before the inbox insert. Neither is an id
        if ((envelope.Id == provisionalId || envelope.Id == Guid.Empty) && message.MessageId.IsNotEmpty())
        {
            envelope.Id = IdFor(subscription, message.MessageId);
        }

        return envelope;
    }

    /// <summary>
    ///     The id of the Pub/Sub message <paramref name="messageId" /> delivered by <paramref name="subscription" />,
    ///     the full resource name <c>projects/{project}/subscriptions/{id}</c>. Never change how it is computed: a
    ///     durable inbox still retains rows under these ids, and a redelivery across the upgrade would be handled again.
    /// </summary>
    internal static Guid IdFor(string subscription, string messageId)
    {
        var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"pubsub:{subscription}:{messageId}"));
        return new Guid(hash.AsSpan(0, 16));
    }
}
