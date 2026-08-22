using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Wolverine.Persistence;

namespace Wolverine.Runtime.Interop.MassTransit;

/// <summary>
/// Reads MassTransit's <c>MessageData&lt;T&gt;</c> wire format on the way in and hydrates the value onto a
/// plain Wolverine property. See GH-3510.
/// </summary>
/// <remarks>
/// MassTransit does not put its claim-check reference in a header the way Wolverine does — it is a JSON
/// object nested in the message body, written by MassTransit's <c>SystemTextJsonMessageDataConverter</c>:
/// <code>
/// { "data-ref": "&lt;address&gt;", "text": "&lt;inline string&gt;", "data": "&lt;inline base64&gt;" }
/// </code>
/// <c>text</c> and <c>data</c> are the inline forms MassTransit uses for payloads under its 4 KB threshold;
/// when either is present the payload never went to the repository and no store lookup is needed.
///
/// This is the read side only. On write the value is emitted as ordinary JSON, exactly as it was before
/// this converter existed — producing MassTransit-compatible references is explicitly out of scope.
/// </remarks>
internal sealed class MassTransitMessageDataConverter<T> : JsonConverter<T>
{
    private const string ReferenceProperty = "data-ref";
    private const string TextProperty = "text";
    private const string DataProperty = "data";

    private readonly IClaimCheckStore _store;
    private readonly Func<Uri, string>? _addressToId;

    public MassTransitMessageDataConverter(IClaimCheckStore store, Func<Uri, string>? addressToId)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _addressToId = addressToId;
    }

    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return default!;

            // Not a MassTransit reference at all -- a plain value written by a Wolverine peer, or an
            // ordinary base64 byte[]. Fall through to the normal representation so a single property can
            // be read from either kind of producer.
            case JsonTokenType.String:
                return fromBytes(readPlainString(ref reader));

            case JsonTokenType.StartObject:
                break;

            default:
                throw new JsonException(
                    $"Unexpected token '{reader.TokenType}' while reading a MassTransit MessageData reference.");
        }

        string? text = null;
        byte[]? inline = null;
        string? address = null;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                continue;
            }

            var name = reader.GetString();
            if (!reader.Read())
            {
                break;
            }

            if (ReferenceProperty.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                address = reader.TokenType == JsonTokenType.Null ? null : reader.GetString();
            }
            else if (TextProperty.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                text = reader.TokenType == JsonTokenType.Null ? null : reader.GetString();
            }
            else if (DataProperty.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                inline = reader.TokenType == JsonTokenType.Null ? null : reader.GetBytesFromBase64();
            }
            else
            {
                reader.Skip();
            }
        }

        // Inline forms win: MassTransit writes them when the payload was small enough to travel in-band,
        // and in that case the repository may hold nothing at all under the address.
        if (text is not null)
        {
            return fromBytes(Encoding.UTF8.GetBytes(text));
        }

        if (inline is not null)
        {
            return fromBytes(inline);
        }

        if (address is null)
        {
            // MassTransit's own converter treats a reference with no address as empty message data.
            return default!;
        }

        return fromBytes(load(new Uri(address, UriKind.RelativeOrAbsolute)));
    }

    private static byte[] readPlainString(ref Utf8JsonReader reader)
    {
        if (typeof(T) == typeof(string))
        {
            return Encoding.UTF8.GetBytes(reader.GetString() ?? string.Empty);
        }

        return reader.GetBytesFromBase64();
    }

    private byte[] load(Uri address)
    {
        bool compressed;
        string id;

        if (_addressToId is not null)
        {
            id = _addressToId(address);
            compressed = id.EndsWith(".gz", StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            id = MassTransitMessageDataAddress.ToPayloadId(address, out compressed);
        }

        // JsonConverter.Read is synchronous, so the store call has to block here. This mirrors the
        // documented blocking calls in ClaimCheckMessageSerializer's sync paths.
#pragma warning disable VSTHRD002
        var bytes = _store
            .LoadAsync(new ClaimCheckToken(id, "application/octet-stream", 0))
            .GetAwaiter().GetResult()
            .ToArray();
#pragma warning restore VSTHRD002

        if (!compressed)
        {
            return bytes;
        }

        // The Azure Storage repository gzips the payload and marks it by appending .gz to the blob name.
        using var source = new MemoryStream(bytes);
        using var gzip = new GZipStream(source, CompressionMode.Decompress);
        using var target = new MemoryStream();
        gzip.CopyTo(target);
        return target.ToArray();
    }

    private static T fromBytes(byte[] bytes)
    {
        if (typeof(T) == typeof(byte[]))
        {
            return (T)(object)bytes;
        }

        if (typeof(T) == typeof(string))
        {
            return (T)(object)Encoding.UTF8.GetString(bytes);
        }

        if (typeof(T) == typeof(ReadOnlyMemory<byte>))
        {
            return (T)(object)new ReadOnlyMemory<byte>(bytes);
        }

        if (typeof(T) == typeof(Stream) || typeof(T) == typeof(MemoryStream))
        {
            return (T)(object)new MemoryStream(bytes, writable: false);
        }

        throw new NotSupportedException(
            $"MassTransit MessageData interop does not support the property type {typeof(T).FullName}. " +
            "Supported types are byte[], string, and Stream.");
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        // Read-side interop only (GH-3510). Emit the value the way it would have been written before this
        // converter was attached so the outbound path is untouched.
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;

            case byte[] bytes:
                writer.WriteBase64StringValue(bytes);
                break;

            case string text:
                writer.WriteStringValue(text);
                break;

            case ReadOnlyMemory<byte> memory:
                writer.WriteBase64StringValue(memory.Span);
                break;

            default:
                throw new NotSupportedException(
                    $"Writing a {typeof(T).FullName} property in MassTransit MessageData format is not supported. " +
                    "Wolverine's MassTransit MessageData interop is a read/consume path only.");
        }
    }
}
