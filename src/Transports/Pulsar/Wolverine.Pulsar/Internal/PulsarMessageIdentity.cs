using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using DotPulsar;
using DotPulsar.Abstractions;

namespace Wolverine.Pulsar.Internal;

/// <summary>
/// Derives the <see cref="Envelope.Id" /> of a Pulsar message that arrives without one of its own from the
/// message's <see cref="MessageId" />, so that every delivery of the same message yields the same id.
///
/// <para>
/// The durable inbox deduplicates on <see cref="Envelope.Id" />, and
/// <see cref="DurabilitySettings.KeepAfterMessageHandling" /> retains a handled row precisely so that a broker
/// redelivery is recognized. A message produced by Wolverine carries its id in the <c>id</c> property. A message
/// from any other producer does not, and used to get a fresh id on every delivery, so an ack timeout, a negative
/// ack, a consumer restart or a subscription reset to an earlier position stored it again and handled it a
/// second time.
/// </para>
///
/// <para>
/// The id is a name-based (version 5, RFC 9562) UUID over the broker, the tenant, the subscription, the topic
/// and the four coordinates of the <see cref="MessageId" />: ledger, entry, partition and batch index. Those
/// coordinates identify one message within one topic of one cluster, and Pulsar keeps them across every
/// redelivery. The broker name and the tenant id tell clusters apart: a broker-per-tenant listener shares the
/// topic and subscription names of the default cluster (GH-3308). The subscription is part of the name because
/// two subscriptions are independent, and each must handle the message once.
/// </para>
///
/// <para>
/// A message moved to a retry letter topic is a new message there, with a new <see cref="MessageId" />, so its
/// derived id is not the original's. That is deliberate: a native reschedule does not settle the original's
/// inbox row, so a retry copy under the original id would be refused as a duplicate of it and never retried.
/// Redeliveries of the retry copy still all share one id, its own.
/// </para>
/// </summary>
internal sealed class PulsarMessageIdentity
{
    /// <summary>
    /// The namespace of these names. Never change it: a different namespace gives every message that a durable
    /// inbox still retains a different id, and its redelivery across the upgrade would be handled again.
    /// </summary>
    internal static readonly Guid Namespace = new("8f3c2b1e-5d4a-4c6b-9e7f-1a2b3c4d5e6f");

    private const int NamespaceLength = 16;
    private const int Sha1Length = 20;

    // Topic names are a few hundred characters at most and the other components are short in practice, so the
    // heap path below is only for pathological names.
    private const int StackBufferLength = 1024;

    // ledger id, entry id, partition, batch index
    private const int CoordinatesLength = sizeof(ulong) + sizeof(ulong) + sizeof(int) + sizeof(int);

    // The namespace followed by the broker, tenant and subscription components, which are fixed per listener
    private readonly byte[] _prefix;

    public PulsarMessageIdentity(string broker, string? tenantId, string subscriptionName)
    {
        var length = NamespaceLength + componentLength(broker) + componentLength(tenantId) +
                     componentLength(subscriptionName);

        _prefix = new byte[length];
        Namespace.TryWriteBytes(_prefix, bigEndian: true, out _);

        var position = NamespaceLength;
        position += writeComponent(_prefix.AsSpan(position), broker);
        position += writeComponent(_prefix.AsSpan(position), tenantId);
        writeComponent(_prefix.AsSpan(position), subscriptionName);
    }

    /// <summary>
    /// The id of <paramref name="messageId" />. <paramref name="consumerTopic" /> names the topic when the message
    /// id does not carry it, as for a message on a single non-partitioned topic.
    /// </summary>
    public Guid IdFor(MessageId messageId, string consumerTopic)
    {
        var topic = string.IsNullOrEmpty(messageId.Topic) ? consumerTopic : messageId.Topic;

        var length = _prefix.Length + componentLength(topic) + CoordinatesLength;
        Span<byte> name = length <= StackBufferLength ? stackalloc byte[StackBufferLength] : new byte[length];
        name = name[..length];

        _prefix.CopyTo(name);
        var position = _prefix.Length;
        position += writeComponent(name[position..], topic);
        BinaryPrimitives.WriteUInt64BigEndian(name[position..], messageId.LedgerId);
        position += sizeof(ulong);
        BinaryPrimitives.WriteUInt64BigEndian(name[position..], messageId.EntryId);
        position += sizeof(ulong);
        BinaryPrimitives.WriteInt32BigEndian(name[position..], messageId.Partition);
        position += sizeof(int);
        BinaryPrimitives.WriteInt32BigEndian(name[position..], messageId.BatchIndex);

        return fromHash(name);
    }

    /// <summary>
    /// Builds the envelope for a consumed message. With an <paramref name="identity" />, a message the mapper gave
    /// no id of its own -- it carries no Wolverine <c>id</c> property, or an unreadable one, and a custom mapper set
    /// none -- gets the id of its <see cref="MessageId" />, so a redelivery of it is the same envelope to the
    /// durable inbox.
    /// </summary>
    internal static PulsarEnvelope CreateEnvelope(IPulsarEnvelopeMapper mapper,
        IMessage<ReadOnlySequence<byte>> message, string consumerTopic, bool isFromRetryConsumer,
        PulsarMessageIdentity? identity)
    {
        var envelope = new PulsarEnvelope(message)
        {
            Data = message.Data.ToArray(),
            IsFromRetryConsumer = isFromRetryConsumer
        };

        // A fresh envelope's id is a new IdGenerator() value nobody else has seen, so the id still being that
        // value after mapping means neither the id property nor a custom mapper supplied one
        var provisionalId = envelope.Id;

        mapper.MapIncomingToEnvelope(envelope, message);

        // PulsarEnvelopeMapper maps a missing or unreadable id property to Guid.Empty instead, which the durable
        // receiver replaces with a fresh IdGenerator() value just before the inbox insert. Neither is an id.
        if (identity != null && (envelope.Id == provisionalId || envelope.Id == Guid.Empty))
        {
            envelope.Id = identity.IdFor(message.MessageId, consumerTopic);
        }

        return envelope;
    }

    /// <summary>
    /// An RFC 9562 version 5 UUID of <paramref name="name" /> in <paramref name="namespace" />.
    /// </summary>
    internal static Guid CreateVersion5(Guid @namespace, ReadOnlySpan<byte> name)
    {
        var buffer = new byte[NamespaceLength + name.Length];
        @namespace.TryWriteBytes(buffer, bigEndian: true, out _);
        name.CopyTo(buffer.AsSpan(NamespaceLength));

        return fromHash(buffer);
    }

    // RFC 9562 section 5.5: SHA-1 of the namespace and the name, truncated to 128 bits, with the version and
    // the variant bits set. SHA-1 is what the RFC prescribes; this is an identifier, not a security boundary.
    private static Guid fromHash(ReadOnlySpan<byte> namespaceAndName)
    {
        Span<byte> hash = stackalloc byte[Sha1Length];
        SHA1.HashData(namespaceAndName, hash);

        hash[6] = (byte)((hash[6] & 0x0F) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);

        return new Guid(hash[..16], bigEndian: true);
    }

    // Every component is length-prefixed, so no two different splits of the same text -- subscription "ab" with
    // topic "c" against subscription "a" with topic "bc" -- can produce the same name. A missing tenant id is
    // written as empty: a tenant id is never the empty string.
    private static int componentLength(string? value)
    {
        return sizeof(int) + (value == null ? 0 : Encoding.UTF8.GetByteCount(value));
    }

    private static int writeComponent(Span<byte> destination, string? value)
    {
        var written = value == null ? 0 : Encoding.UTF8.GetBytes(value, destination[sizeof(int)..]);
        BinaryPrimitives.WriteInt32BigEndian(destination, written);

        return sizeof(int) + written;
    }
}
