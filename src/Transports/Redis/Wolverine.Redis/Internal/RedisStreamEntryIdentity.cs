using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using StackExchange.Redis;

namespace Wolverine.Redis.Internal;

/// <summary>
/// Derives the <see cref="Envelope.Id" /> of a Redis stream entry that arrives without one of its own from where
/// the entry sits in its stream, so that every delivery of the same entry yields the same id.
///
/// <para>
/// The durable inbox deduplicates on <see cref="Envelope.Id" />, and
/// <see cref="DurabilitySettings.KeepAfterMessageHandling" /> retains a handled row precisely so that a broker
/// redelivery is recognized. An entry written by Wolverine carries its id in the <c>wolverine-id</c> field. An
/// entry from any other producer does not, and used to get a fresh id on every delivery, so a pending entry
/// reclaimed with <c>XAUTOCLAIM</c> after its consumer died before acknowledging it, or an entry read again after
/// the consumer group was moved back with <c>XGROUP SETID</c>, was stored again and handled a second time.
/// </para>
///
/// <para>
/// The id is a name-based (version 5, RFC 9562) UUID over the broker, the tenant, the database, the consumer
/// group, the stream key and the entry id. An entry id such as <c>1700000000000-0</c> identifies one entry
/// within one stream of one Redis server, and it never changes: a reclaimed or re-read entry is the same entry
/// with the same id. The broker name and the tenant id tell servers apart, since a broker-per-tenant listener
/// reads the same stream key with the same group on the tenant's own server (GH-3309), and the database number
/// tells apart the same stream key in two databases of one server. The consumer group is part of the name
/// because two groups are two independent subscriptions, and each must handle the entry once.
/// </para>
/// </summary>
internal sealed class RedisStreamEntryIdentity
{
    /// <summary>
    /// The namespace of these names. Never change it: a different namespace gives every entry that a durable
    /// inbox still retains a different id, and its redelivery across the upgrade would be handled again.
    /// </summary>
    internal static readonly Guid Namespace = new("8d2f3c1e-6b0a-4f7e-9a51-2c4e7d90b3a6");

    private const int NamespaceLength = 16;
    private const int Sha1Length = 20;

    // Entry ids are at most 41 characters ("<ms>-<seq>" with two 64-bit numbers) and stream keys and group names
    // are short in practice, so the heap path below is only for pathological names
    private const int StackBufferLength = 512;

    // The namespace followed by every component except the entry id, all of which are fixed per listener
    private readonly byte[] _prefix;

    public RedisStreamEntryIdentity(string broker, string? tenantId, int databaseId, string consumerGroup,
        string streamKey)
    {
        var length = NamespaceLength + componentLength(broker) + componentLength(tenantId) + sizeof(int) +
                     componentLength(consumerGroup) + componentLength(streamKey);

        _prefix = new byte[length];
        Namespace.TryWriteBytes(_prefix, bigEndian: true, out _);

        var position = NamespaceLength;
        position += writeComponent(_prefix.AsSpan(position), broker);
        position += writeComponent(_prefix.AsSpan(position), tenantId);
        BinaryPrimitives.WriteInt32BigEndian(_prefix.AsSpan(position), databaseId);
        position += sizeof(int);
        position += writeComponent(_prefix.AsSpan(position), consumerGroup);
        writeComponent(_prefix.AsSpan(position), streamKey);
    }

    /// <summary>
    /// The identity for the entries a listener consumes. <paramref name="connection" /> is the tenant's own
    /// multiplexer for a broker-per-tenant listener, and null for the listener on the transport's own connection.
    /// </summary>
    internal static RedisStreamEntryIdentity For(RedisTransport transport, RedisStreamEndpoint endpoint,
        IConnectionMultiplexer? connection)
    {
        string? tenantId = null;
        if (connection != null)
        {
            var tenant = transport.Tenants.FirstOrDefault(x => ReferenceEquals(x.Connection, connection));

            // Without the tenant id, the same entry id of the same stream on two tenants' servers would be one
            // entry to the inbox, and one of them would be discarded as a duplicate of the other
            tenantId = tenant?.TenantId ?? throw new InvalidOperationException(
                $"The Redis transport '{transport.Protocol}' has no tenant whose connection is the one this listener consumes");
        }

        return new RedisStreamEntryIdentity(transport.Protocol, tenantId, endpoint.DatabaseId,
            endpoint.ConsumerGroup!, endpoint.StreamKey);
    }

    public Guid IdFor(string entryId)
    {
        var length = _prefix.Length + componentLength(entryId);
        Span<byte> name = length <= StackBufferLength ? stackalloc byte[StackBufferLength] : new byte[length];
        name = name[..length];

        _prefix.CopyTo(name);
        writeComponent(name[_prefix.Length..], entryId);

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

    // Every component is length-prefixed, so no two different splits of the same text -- group "ab" with stream
    // "c" against group "a" with stream "bc" -- can produce the same name. A missing tenant is written as empty:
    // a tenant id is never the empty string.
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

internal static class RedisStreamEntryEnvelopes
{
    /// <summary>
    /// Builds the envelope for a stream entry, whether it came from <c>XREADGROUP</c> or was reclaimed with
    /// <c>XAUTOCLAIM</c>. With an <paramref name="identity" />, an entry the mapper gave no id of its own -- it
    /// carries no Wolverine <c>wolverine-id</c> field, and a custom mapper set none -- gets the id of its position
    /// in the stream, so a redelivery of it is the same envelope to the durable inbox.
    /// </summary>
    internal static Envelope CreateEnvelope(this IRedisEnvelopeMapper mapper, string streamKey, StreamEntry entry,
        RedisStreamEntryIdentity? identity)
    {
        var envelope = new Envelope { TopicName = streamKey };

        // A fresh envelope's id is a new IdGenerator() value nobody else has seen, so the id still being that
        // value after mapping means neither the id field nor a custom mapper supplied one
        var provisionalId = envelope.Id;

        mapper.MapIncomingToEnvelope(envelope, entry);

        // RedisEnvelopeMapper maps a missing or unreadable id field to Guid.Empty instead, which the durable
        // receiver replaces with a fresh IdGenerator() value just before the inbox insert. Neither is an id.
        if (identity != null && (envelope.Id == provisionalId || envelope.Id == Guid.Empty))
        {
            envelope.Id = identity.IdFor(entry.Id.ToString());
        }

        return envelope;
    }
}
