using System.IO.Compression;

namespace Wolverine.Runtime.Serialization;

/// <summary>
/// GH-4720. <see cref="IntrinsicSerializer" />'s compressing twin, under its own content type.
/// </summary>
/// <remarks>
/// <para>
/// Agent command payloads are lists of agent URIs — <c>AgentUriList.Write</c> emits them as delimited UTF-8 —
/// and a list of thousands of sibling agents is nearly all repeated structure: scheme, family, projection
/// name, database name. Measured 3.9x on a GUID-dominated shape up to 24x on a structural one, at about 5ms
/// for 7,592 agents. On the cluster that reported GH-4718 (512 databases, ~2,200 tenants, 37,000–58,000
/// agents) that is the difference between a control queue row of several MB and one of a few hundred KB.
/// </para>
/// <para>
/// The format is chosen by CONTENT TYPE rather than by sniffing bytes inside the payload, so a node that does
/// not know this serializer fails to resolve one at all — a loud, named failure — instead of handing gzip
/// bytes to a UTF-8 reader and misparsing them. That is also why this is a separate serializer rather than a
/// flag on the intrinsic one: the content type IS the version marker, and it rides the wire in the envelope
/// header where the reader already looks.
/// </para>
/// <para>
/// Within that, the leading framing byte lets a payload too small to be worth compressing travel
/// uncompressed. gzip has ~20 bytes of fixed overhead, so a two-agent command would otherwise grow; the point
/// is to help the batches that are large without taxing the ones that are not.
/// </para>
/// </remarks>
public sealed class CompressedIntrinsicSerializer : IMessageSerializer
{
    public const string MimeType = "binary/wolverine+gzip";

    /// <summary>
    /// Payloads at or below this many bytes travel uncompressed behind the <see cref="Raw" /> marker. Well
    /// under any realistic agent batch, and comfortably above gzip's own fixed overhead.
    /// </summary>
    internal const int CompressionThreshold = 512;

    private const byte Raw = 0;
    private const byte GZipped = 1;

    public static readonly CompressedIntrinsicSerializer Instance = new();

    private CompressedIntrinsicSerializer()
    {
    }

    public string ContentType => MimeType;

    public byte[] Write(Envelope envelope)
    {
        return Pack(IntrinsicSerializer.Instance.Write(envelope));
    }

    public object ReadFromData(Type messageType, Envelope envelope)
    {
        return IntrinsicSerializer.Instance.SerializerFor(messageType).ReadFromData(Unpack(envelope.Data!));
    }

    internal static byte[] Pack(byte[] raw)
    {
        if (raw.Length <= CompressionThreshold)
        {
            var passthrough = new byte[raw.Length + 1];
            passthrough[0] = Raw;
            raw.CopyTo(passthrough, 1);
            return passthrough;
        }

        using var output = new MemoryStream();
        output.WriteByte(GZipped);

        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, true))
        {
            gzip.Write(raw, 0, raw.Length);
        }

        var packed = output.ToArray();

        // Incompressible data — already-compressed bytes, or a pathological URI set — can come out larger.
        // Falling back keeps this from ever making a payload worse than not having the feature on.
        if (packed.Length >= raw.Length + 1)
        {
            var passthrough = new byte[raw.Length + 1];
            passthrough[0] = Raw;
            raw.CopyTo(passthrough, 1);
            return passthrough;
        }

        return packed;
    }

    internal static byte[] Unpack(byte[] data)
    {
        if (data.Length == 0)
        {
            throw new WolverineSerializationException(
                $"An empty payload cannot be read as {MimeType}, which always carries a leading framing byte.");
        }

        switch (data[0])
        {
            case Raw:
                return data[1..];

            case GZipped:
                using (var input = new MemoryStream(data, 1, data.Length - 1))
                using (var gzip = new GZipStream(input, CompressionMode.Decompress))
                using (var output = new MemoryStream())
                {
                    gzip.CopyTo(output);
                    return output.ToArray();
                }

            default:
                throw new WolverineSerializationException(
                    $"Unknown {MimeType} framing marker {data[0]}. Expected {Raw} (raw) or {GZipped} (gzip).");
        }
    }

    public object ReadFromData(byte[] data)
    {
        throw new NotSupportedException();
    }

    public byte[] WriteMessage(object message)
    {
        throw new NotSupportedException();
    }
}
