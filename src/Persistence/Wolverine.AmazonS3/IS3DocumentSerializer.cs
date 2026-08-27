using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Text.Json;

namespace Wolverine.AmazonS3;

/// <summary>
/// How a document's bytes are written to and read back from an S3 object. Supply your own on a
/// <see cref="S3DocumentMapping" /> for a different format, an envelope around the payload, a
/// checksum, or encryption.
/// </summary>
public interface IS3DocumentSerializer
{
    /// <summary>Written as the object's Content-Type.</summary>
    string ContentType { get; }

    /// <summary>Written as the object's Content-Encoding when non-null.</summary>
    string? ContentEncoding { get; }

    ReadOnlyMemory<byte> Serialize<T>(T document) where T : class;

    T? Deserialize<T>(ReadOnlyMemory<byte> data) where T : class;
}

public enum S3Compression
{
    None,
    Brotli,
    GZip
}

/// <summary>
/// The default serializer: System.Text.Json, optionally compressed.
/// </summary>
/// <remarks>
/// Calls the reflection-based <see cref="JsonSerializer" /> overloads, as Wolverine's own default
/// serializer does, so the trim and AOT warnings are suppressed at the leaf. An AOT-clean application
/// should supply an <see cref="IS3DocumentSerializer" /> wrapping
/// <c>JsonSerializer.Serialize&lt;T&gt;(value, JsonTypeInfo)</c> — see the AOT publishing guide.
/// </remarks>
public class S3DocumentSerializer : IS3DocumentSerializer
{
    private readonly S3Compression _compression;
    private readonly JsonSerializerOptions _options;

    public S3DocumentSerializer(JsonSerializerOptions? options = null,
        S3Compression compression = S3Compression.None)
    {
        _options = options ?? new JsonSerializerOptions(JsonSerializerDefaults.Web);
        _compression = compression;
    }

    public static IS3DocumentSerializer Default { get; } = new S3DocumentSerializer();

    public string ContentType => "application/json";

    public string? ContentEncoding => _compression switch
    {
        S3Compression.Brotli => "br",
        S3Compression.GZip => "gzip",
        _ => null
    };

    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "Default document serializer; AOT consumers supply an IS3DocumentSerializer wrapping JsonTypeInfo. See AOT guide.")]
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "Default document serializer; AOT consumers supply an IS3DocumentSerializer wrapping JsonTypeInfo. See AOT guide.")]
    public ReadOnlyMemory<byte> Serialize<T>(T document) where T : class
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(document, _options);

        if (_compression == S3Compression.None)
        {
            return json;
        }

        using var output = new MemoryStream();
        using (var compressor = compressing(output))
        {
            compressor.Write(json, 0, json.Length);
        }

        return output.ToArray();
    }

    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "Default document serializer; AOT consumers supply an IS3DocumentSerializer wrapping JsonTypeInfo. See AOT guide.")]
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "Default document serializer; AOT consumers supply an IS3DocumentSerializer wrapping JsonTypeInfo. See AOT guide.")]
    public T? Deserialize<T>(ReadOnlyMemory<byte> data) where T : class
    {
        if (_compression == S3Compression.None)
        {
            return JsonSerializer.Deserialize<T>(data.Span, _options);
        }

        using var input = new MemoryStream(data.ToArray(), false);
        using var decompressor = decompressing(input);
        using var output = new MemoryStream();
        decompressor.CopyTo(output);

        return JsonSerializer.Deserialize<T>(output.ToArray(), _options);
    }

    private Stream compressing(Stream output)
    {
        return _compression == S3Compression.Brotli
            ? new BrotliStream(output, CompressionMode.Compress, true)
            : new GZipStream(output, CompressionMode.Compress, true);
    }

    private Stream decompressing(Stream input)
    {
        return _compression == S3Compression.Brotli
            ? new BrotliStream(input, CompressionMode.Decompress, true)
            : new GZipStream(input, CompressionMode.Decompress, true);
    }
}
