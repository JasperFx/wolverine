using Amazon.SQS.Model;
using Wolverine.Runtime;

namespace Wolverine.AmazonSqs.Internal;

/// <summary>
///     Splits an outgoing message body that would exceed SQS's hard size limit into several SQS
///     messages, and puts them back together on the receiving side.
/// </summary>
/// <remarks>
///     <para>
///     <b>Wolverine to Wolverine only.</b> The fragments are Wolverine's own framing, carried in SQS
///     message attributes; a non-Wolverine consumer reading this queue sees N unintelligible messages
///     rather than one. That is why it is opt-in per endpoint rather than automatic.
///     </para>
///     <para>
///     <b>Reassembly is per listener, in memory.</b> SQS is a competing-consumer queue: with several
///     nodes on one standard queue, fragments of a single message can be delivered to different nodes
///     and no one of them ever holds the full set. The endpoint configuration documents the three
///     shapes where that cannot happen — a FIFO queue (a message group is delivered to one consumer at
///     a time), a <c>GlobalPartitioning</c> listener (the group id's owner gets every fragment), or a
///     single listening node.
///     </para>
/// </remarks>
internal static class SqsMessageFragments
{
    /// <summary>
    ///     SQS rejects any message whose body plus attributes exceeds 256KB, and the rejection is a
    ///     <c>SenderFault</c> — permanent, not worth retrying.
    /// </summary>
    internal const int MaximumMessageBytes = 262_144;

    /// <summary>
    ///     The largest body Wolverine will hand SQS as a single message. Under
    ///     <see cref="MaximumMessageBytes" /> by an allowance for the message attributes, which count
    ///     against the same budget.
    /// </summary>
    /// <remarks>
    ///     This, and not <see cref="FragmentBodyBytes" />, is what decides whether a message needs
    ///     splitting at all. A body between the two is perfectly legal for SQS and must keep going out as
    ///     one message exactly as it always has.
    /// </remarks>
    internal const int MaximumBodyBytes = MaximumMessageBytes - 4096;

    /// <summary>
    ///     Bytes of one fragment's body. Deliberately well under <see cref="MaximumBodyBytes" />: the
    ///     fragment attributes and the SQS entry scaffolding all ride along, and under-estimating bounces
    ///     the send rather than costing an extra message.
    /// </summary>
    internal const int FragmentBodyBytes = 200 * 1024;

    /// <summary>
    ///     Refuse to shard past this. A message needing more fragments than this is a claim-check
    ///     problem, not a framing problem, and turning one 50MB message into 250 SQS messages would be
    ///     an expensive way to find that out.
    /// </summary>
    internal const int MaximumFragments = 10;

    internal const string FragmentIdAttribute = "wolverine-fragment-id";
    internal const string FragmentIndexAttribute = "wolverine-fragment-index";
    internal const string FragmentCountAttribute = "wolverine-fragment-count";

    /// <summary>
    ///     SQS returns only the message attributes a <c>ReceiveMessage</c> explicitly names, so a listener
    ///     has to ask for these or a fragmented message arrives looking like N unrelated whole messages,
    ///     each of which fails to deserialize.
    /// </summary>
    internal static readonly string[] AttributeNames =
        [FragmentIdAttribute, FragmentIndexAttribute, FragmentCountAttribute];

    /// <summary>
    ///     Is this body too big for SQS to accept as one message?
    /// </summary>
    internal static bool ExceedsLimit(string body) => body.Length > MaximumBodyBytes;

    /// <summary>
    ///     Split a message body into fragments. Splits on the ALREADY-ENCODED body rather than on the
    ///     envelope's bytes, so that whatever the endpoint's <see cref="ISqsEnvelopeMapper" /> produced
    ///     is what gets reassembled — the mapper is not asked to know anything about fragmenting.
    /// </summary>
    internal static string[] Split(string body)
    {
        var count = (body.Length + FragmentBodyBytes - 1) / FragmentBodyBytes;

        var fragments = new string[count];
        for (var i = 0; i < count; i++)
        {
            var start = i * FragmentBodyBytes;
            var length = Math.Min(FragmentBodyBytes, body.Length - start);
            fragments[i] = body.Substring(start, length);
        }

        return fragments;
    }

    internal static Dictionary<string, MessageAttributeValue> AttributesFor(Guid fragmentId, int index, int count)
    {
        return new Dictionary<string, MessageAttributeValue>
        {
            [FragmentIdAttribute] = new() { StringValue = fragmentId.ToString(), DataType = "String" },
            [FragmentIndexAttribute] = new() { StringValue = index.ToString(), DataType = "Number" },
            [FragmentCountAttribute] = new() { StringValue = count.ToString(), DataType = "Number" }
        };
    }

    /// <summary>
    ///     Read the fragment framing off a received message, if it has any.
    /// </summary>
    internal static bool TryReadHeader(Message message, out SqsFragmentHeader header)
    {
        header = default;

        if (message.MessageAttributes == null) return false;

        if (!message.MessageAttributes.TryGetValue(FragmentIdAttribute, out var id)) return false;
        if (!message.MessageAttributes.TryGetValue(FragmentIndexAttribute, out var index)) return false;
        if (!message.MessageAttributes.TryGetValue(FragmentCountAttribute, out var count)) return false;

        if (!Guid.TryParse(id.StringValue, out var fragmentId)) return false;
        if (!int.TryParse(index.StringValue, out var i)) return false;
        if (!int.TryParse(count.StringValue, out var n)) return false;

        if (n <= 0 || i < 0 || i >= n) return false;

        header = new SqsFragmentHeader(fragmentId, i, n);
        return true;
    }

    /// <summary>
    ///     The group id every fragment of one message must share, so that a FIFO queue keeps them on one
    ///     consumer and a <c>GlobalPartitioning</c> listener routes them all to the node that owns the
    ///     group.
    /// </summary>
    /// <remarks>
    ///     The envelope's own <see cref="Envelope.GroupId" /> wins when it has one — the caller has
    ///     already said which partition this message belongs to, and overriding it would send the
    ///     fragments somewhere other than where the whole message was meant to go. Only when there is no
    ///     group id at all does the fragment id stand in, which at least keeps the set together.
    /// </remarks>
    internal static string GroupIdFor(Envelope envelope, Guid fragmentId)
    {
        return string.IsNullOrEmpty(envelope.GroupId)
            ? "wolverine-fragments-" + fragmentId
            : envelope.GroupId;
    }
}

internal readonly record struct SqsFragmentHeader(Guid FragmentId, int Index, int Count);
