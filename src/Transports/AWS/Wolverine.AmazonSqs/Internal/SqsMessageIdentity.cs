using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Wolverine.AmazonSqs.Internal;

/// <summary>
/// Derives the <see cref="Envelope.Id" /> of an SQS message that arrives without one of its own from the identity
/// the broker gave it, so that every delivery of the same message yields the same id.
///
/// <para>
/// The durable inbox deduplicates on <see cref="Envelope.Id" />, and
/// <see cref="DurabilitySettings.KeepAfterMessageHandling" /> retains a handled row precisely so that a broker
/// redelivery is recognized. A message sent by Wolverine carries its id inside the Wolverine envelope. A message
/// from any other producer -- raw JSON, a CloudEvent whose id is not a UUID, a custom mapper that sets no id --
/// used to get a fresh id on every delivery, so a delete that never landed (a crash right after the inbox insert,
/// a lost DeleteMessage call) made the redelivery at the visibility timeout a brand new envelope, stored again and
/// handled a second time.
/// </para>
///
/// <para>
/// The id is a name-based (version 5, RFC 9562) UUID over the broker, the tenant, the queue and the broker's own
/// identity of the message, which is one of:
/// </para>
/// <list type="bullet">
/// <item>
/// The SNS <c>MessageId</c> of the notification, for a message an SNS subscription delivered without raw message
/// delivery. SNS is at-least-once too: it can deliver one notification to the same queue twice, as two SQS
/// messages with two different SQS message ids. The notification's own id is the same on both.
/// </item>
/// <item>
/// The fragment set id of a message the sender split into several SQS messages (GH-3926), which every fragment
/// carries and which no single fragment's SQS message id stands for.
/// </item>
/// <item>The SQS <c>MessageId</c> otherwise, which SQS keeps across every redelivery of one message.</item>
/// </list>
/// <para>
/// The queue is part of the name because one SNS notification fans out to every subscribed queue under the same
/// SNS message id, and each queue's listener must handle it once -- under the default
/// <see cref="MessageIdentity.IdOnly" /> the inbox would otherwise refuse the second
/// queue's copy as a duplicate of the first. The broker name and the tenant id tell apart queues of the same name
/// on a named broker or on a tenant's own account. The kind of identity is part of the name too, so an SNS
/// message id can never produce the id of an SQS message id that happens to share its text.
/// </para>
/// </summary>
internal sealed class SqsMessageIdentity
{
    /// <summary>
    /// The namespace of these names. Never change it: a different namespace gives every message that a durable
    /// inbox still retains a different id, and its redelivery across the upgrade would be handled again.
    /// </summary>
    internal static readonly Guid Namespace = new("6b0f2f5e-2c87-4d3e-9a51-7f4c1d2e8a93");

    internal const string SqsMessageKind = "sqs";
    internal const string SnsMessageKind = "sns";
    internal const string FragmentSetKind = "fragments";

    private const int NamespaceLength = 16;
    private const int Sha1Length = 20;

    // Queue names are at most 80 characters and message ids are 36, so the heap path below is only for
    // pathological broker or tenant names
    private const int StackBufferLength = 512;

    // The namespace followed by the broker, tenant and queue components, which are fixed per listener
    private readonly byte[] _prefix;

    public SqsMessageIdentity(string broker, string? tenantId, string queueName)
    {
        var length = NamespaceLength + componentLength(broker) + componentLength(tenantId) +
                     componentLength(queueName);

        _prefix = new byte[length];
        Namespace.TryWriteBytes(_prefix, bigEndian: true, out _);

        var position = NamespaceLength;
        position += writeComponent(_prefix.AsSpan(position), broker);
        position += writeComponent(_prefix.AsSpan(position), tenantId);
        writeComponent(_prefix.AsSpan(position), queueName);
    }

    /// <summary>
    /// Give <paramref name="envelope" /> the id derived from its broker identity, unless mapping it already gave
    /// it an id of its own. <paramref name="provisionalId" /> is the id the envelope was created with, before the
    /// mapper ran.
    /// </summary>
    internal void AssignIdIfMissing(AmazonSqsEnvelope envelope, Guid provisionalId, SqsFragmentHeader? fragments)
    {
        // A fresh envelope's id is a new IdGenerator() value nobody else has seen, so the id still being that
        // value after mapping means neither the message nor a custom mapper supplied one. Guid.Empty is not an
        // id either: the durable receiver replaces it with a fresh one just before the inbox insert.
        if (envelope.Id != provisionalId && envelope.Id != Guid.Empty) return;

        envelope.Id = IdFor(envelope, fragments);
    }

    internal Guid IdFor(AmazonSqsEnvelope envelope, SqsFragmentHeader? fragments)
    {
        if (envelope.SnsMessageId is { Length: > 0 } snsMessageId)
        {
            return IdFor(SnsMessageKind, snsMessageId);
        }

        if (fragments is { } header)
        {
            return IdFor(FragmentSetKind, header.FragmentId.ToString());
        }

        return IdFor(SqsMessageKind, envelope.SqsMessage.MessageId);
    }

    internal Guid IdFor(string kind, string? messageId)
    {
        var length = _prefix.Length + componentLength(kind) + componentLength(messageId);
        Span<byte> name = length <= StackBufferLength ? stackalloc byte[StackBufferLength] : new byte[length];
        name = name[..length];

        _prefix.CopyTo(name);
        var position = _prefix.Length;
        position += writeComponent(name[position..], kind);
        writeComponent(name[position..], messageId);

        return fromHash(name);
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

    // Every component is length-prefixed, so no two different splits of the same text -- queue "ab" with message
    // id "c" against queue "a" with message id "bc" -- can produce the same name. A missing component is written
    // as empty: a tenant id is never the empty string.
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
