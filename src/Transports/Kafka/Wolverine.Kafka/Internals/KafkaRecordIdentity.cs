using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Wolverine.Kafka.Internals;

/// <summary>
/// Derives the <see cref="Envelope.Id" /> of a Kafka record that arrives without one of its own from where the
/// record sits in the log, so that every read of the same record yields the same id.
///
/// <para>
/// The durable inbox deduplicates on <see cref="Envelope.Id" />, and
/// <see cref="DurabilitySettings.KeepAfterMessageHandling" /> retains a handled row precisely so that a broker
/// redelivery is recognized. A record produced by Wolverine carries its id in the <c>id</c> header. A record
/// from any other producer does not, and used to get a fresh id on every read, so a consumer restart, a
/// rebalance, a rewind to an older committed offset or a listener restarted after an inbox outage stored it
/// again and handled it a second time.
/// </para>
///
/// <para>
/// The id is a name-based (version 5, RFC 9562) UUID over the broker, the tenant, the consumer group, the
/// topic, the partition and the offset. An offset identifies one record within one topic partition of one
/// cluster. The broker name and the tenant id tell clusters apart: a broker-per-tenant listener shares the
/// topic names and the group id of the default cluster (GH-3303). The consumer group is part of the name
/// because two groups are two independent subscriptions, and each must handle the record once.
/// </para>
/// </summary>
internal sealed class KafkaRecordIdentity
{
    /// <summary>
    /// The namespace of these names. Never change it: a different namespace gives every record that a durable
    /// inbox still retains a different id, and its redelivery across the upgrade would be handled again.
    /// </summary>
    internal static readonly Guid Namespace = new("04c92654-fb88-491a-a00f-3935b5dc8935");

    private const int NamespaceLength = 16;
    private const int Sha1Length = 20;

    // Topic names are at most 249 characters and group and tenant ids are short in practice, so the heap
    // path below is only for pathological names.
    private const int StackBufferLength = 512;

    // The namespace followed by the broker, tenant and group components, which are fixed per listener
    private readonly byte[] _prefix;

    public KafkaRecordIdentity(string broker, string? tenantId, string? groupId)
    {
        var length = NamespaceLength + componentLength(broker) + componentLength(tenantId) +
                     componentLength(groupId);

        _prefix = new byte[length];
        Namespace.TryWriteBytes(_prefix, bigEndian: true, out _);

        var position = NamespaceLength;
        position += writeComponent(_prefix.AsSpan(position), broker);
        position += writeComponent(_prefix.AsSpan(position), tenantId);
        writeComponent(_prefix.AsSpan(position), groupId);
    }

    /// <summary>
    /// The identity for the records a listener consumes. <paramref name="tenantTransport" /> is the tenant's own
    /// cluster for a broker-per-tenant listener, and null for the listener on the transport's own cluster.
    /// </summary>
    internal static KafkaRecordIdentity For(KafkaTransport transport, KafkaTransport? tenantTransport,
        KafkaConsumerOptions config)
    {
        string? tenantId = null;
        if (tenantTransport != null)
        {
            var tenant = transport.Tenants.FirstOrDefault(x => ReferenceEquals(x.Transport, tenantTransport));

            // Without the tenant id, the same topic, partition and offset on two tenants' clusters would be one
            // record to the inbox, and one of them would be discarded as a duplicate of the other
            tenantId = tenant?.TenantId ?? throw new InvalidOperationException(
                $"The Kafka transport '{transport.Protocol}' has no tenant whose cluster is the one this listener consumes");
        }

        return new KafkaRecordIdentity(transport.Protocol, tenantId, config.GroupId);
    }

    public Guid IdFor(string topic, int partition, long offset)
    {
        var length = _prefix.Length + componentLength(topic) + sizeof(int) + sizeof(long);
        Span<byte> name = length <= StackBufferLength ? stackalloc byte[StackBufferLength] : new byte[length];
        name = name[..length];

        _prefix.CopyTo(name);
        var position = _prefix.Length;
        position += writeComponent(name[position..], topic);
        BinaryPrimitives.WriteInt32BigEndian(name[position..], partition);
        position += sizeof(int);
        BinaryPrimitives.WriteInt64BigEndian(name[position..], offset);

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

    // Every component is length-prefixed, so no two different splits of the same text -- group "ab" with topic
    // "c" against group "a" with topic "bc" -- can produce the same name. A missing component is written as
    // empty: a tenant id or a group id is never the empty string.
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
