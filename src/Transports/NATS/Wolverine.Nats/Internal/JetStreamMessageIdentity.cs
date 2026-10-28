using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Wolverine.Configuration;

namespace Wolverine.Nats.Internal;

/// <summary>
/// Derives the <see cref="Envelope.Id" /> of a JetStream message that arrives without one of its own from where the
/// message sits in its stream, so that every delivery of the same message to the same consumer yields the same id.
///
/// <para>
/// The durable inbox deduplicates on <see cref="Envelope.Id" />, and
/// <see cref="DurabilitySettings.KeepAfterMessageHandling" /> retains a handled row precisely so that a broker
/// redelivery is recognized. A message published by Wolverine carries its id in the <c>id</c> header. A message
/// from any other publisher does not, and used to get a fresh id on every delivery, so a redelivery after
/// <c>AckWait</c> expired, a lost ack, or a consumer replaying from an earlier position stored it again and handled
/// it a second time.
/// </para>
///
/// <para>
/// The id is a name-based (version 5, RFC 9562) UUID over the transport, the tenant, the stream, the moment the
/// stream was created, the consumer and the stream sequence. A stream sequence identifies one message within one
/// stream, and stays the same on every redelivery. The transport name (a named broker) and the tenant id (a tenant
/// with its own connection) tell servers apart that may hold streams of the same name. The stream's creation time
/// tells a stream apart from a deleted one of the same name, whose sequences started over at 1: without it, a new
/// message would take the id of a handled one the inbox still retains, and be discarded as its duplicate. The
/// consumer is part of the name because two consumers on one stream are two independent subscriptions, and each
/// must handle the message once.
/// </para>
/// </summary>
internal sealed class JetStreamMessageIdentity
{
    /// <summary>
    /// The namespace of these names. Never change it: a different namespace gives every message that a durable
    /// inbox still retains a different id, and its redelivery across the upgrade would be handled again.
    /// </summary>
    internal static readonly Guid Namespace = new("8f8b9c32-95aa-4f27-b44f-b5087c0b2830");

    private const int NamespaceLength = 16;
    private const int Sha1Length = 20;

    // The namespace and every component but the stream sequence, all of which are fixed per listener
    private readonly byte[] _prefix;

    public JetStreamMessageIdentity(string transport, string? tenantId, string stream, DateTimeOffset? streamCreated,
        string consumer)
    {
        var length = NamespaceLength + componentLength(transport) + componentLength(tenantId) +
                     componentLength(stream) + sizeof(long) + componentLength(consumer);

        _prefix = new byte[length];
        Namespace.TryWriteBytes(_prefix, bigEndian: true, out _);

        var position = NamespaceLength;
        position += writeComponent(_prefix.AsSpan(position), transport);
        position += writeComponent(_prefix.AsSpan(position), tenantId);
        position += writeComponent(_prefix.AsSpan(position), stream);

        // Zero when the stream's creation time could not be read: no real stream was created at the epoch
        BinaryPrimitives.WriteInt64BigEndian(_prefix.AsSpan(position), streamCreated?.UtcTicks ?? 0);
        position += sizeof(long);

        writeComponent(_prefix.AsSpan(position), consumer);
    }

    /// <summary>
    /// Only the durable inbox deduplicates on the envelope id. Anywhere else a derived id would be a hash per message
    /// on the hot path that buys nothing: a Buffered listener acks on receipt and keeps nothing to compare against,
    /// and Inline and NativeAck settle with the broker after handling, with no store of what was handled.
    /// </summary>
    internal static bool AppliesTo(Endpoint endpoint)
    {
        return endpoint.Mode == EndpointMode.Durable;
    }

    public Guid IdFor(ulong streamSequence)
    {
        var length = _prefix.Length + sizeof(ulong);
        Span<byte> name = length <= 512 ? stackalloc byte[length] : new byte[length];

        _prefix.CopyTo(name);
        BinaryPrimitives.WriteUInt64BigEndian(name[_prefix.Length..], streamSequence);

        return fromHash(name);
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

    // RFC 9562 section 5.5: SHA-1 of the namespace and the name, truncated to 128 bits, with the version and the
    // variant bits set. SHA-1 is what the RFC prescribes; this is an identifier, not a security boundary.
    private static Guid fromHash(ReadOnlySpan<byte> namespaceAndName)
    {
        Span<byte> hash = stackalloc byte[Sha1Length];
        SHA1.HashData(namespaceAndName, hash);

        hash[6] = (byte)((hash[6] & 0x0F) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);

        return new Guid(hash[..16], bigEndian: true);
    }

    // Every component is length-prefixed, so no two different splits of the same text -- stream "ab" with consumer
    // "c" against stream "a" with consumer "bc" -- can produce the same name. A missing tenant id is written as
    // empty: a tenant id is never the empty string.
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
