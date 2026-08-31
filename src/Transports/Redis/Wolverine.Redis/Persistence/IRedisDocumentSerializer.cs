using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace Wolverine.Redis;

/// <summary>
/// How a document's bytes are written to and read back from Redis. Supply your own on a
/// <see cref="RedisDocumentMapping" /> for a different format, an envelope around the payload, or
/// encryption.
/// </summary>
public interface IRedisDocumentSerializer
{
    ReadOnlyMemory<byte> Serialize<T>(T document) where T : class;

    T? Deserialize<T>(ReadOnlyMemory<byte> data) where T : class;
}

/// <summary>
/// The default serializer: System.Text.Json.
/// </summary>
/// <remarks>
/// Calls the reflection-based <see cref="JsonSerializer" /> overloads, as Wolverine's own default
/// serializer does, so the trim and AOT warnings are suppressed at the leaf. An AOT-clean application
/// should supply an <see cref="IRedisDocumentSerializer" /> wrapping
/// <c>JsonSerializer.Serialize&lt;T&gt;(value, JsonTypeInfo)</c> — see the AOT publishing guide.
/// </remarks>
public class RedisDocumentSerializer : IRedisDocumentSerializer
{
    private readonly JsonSerializerOptions _options;

    public RedisDocumentSerializer(JsonSerializerOptions? options = null)
    {
        _options = options ?? new JsonSerializerOptions(JsonSerializerDefaults.Web);
    }

    public static IRedisDocumentSerializer Default { get; } = new RedisDocumentSerializer();

    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "Default document serializer; AOT consumers supply an IRedisDocumentSerializer wrapping JsonTypeInfo. See AOT guide.")]
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "Default document serializer; AOT consumers supply an IRedisDocumentSerializer wrapping JsonTypeInfo. See AOT guide.")]
    public ReadOnlyMemory<byte> Serialize<T>(T document) where T : class
    {
        return JsonSerializer.SerializeToUtf8Bytes(document, _options);
    }

    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "Default document serializer; AOT consumers supply an IRedisDocumentSerializer wrapping JsonTypeInfo. See AOT guide.")]
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "Default document serializer; AOT consumers supply an IRedisDocumentSerializer wrapping JsonTypeInfo. See AOT guide.")]
    public T? Deserialize<T>(ReadOnlyMemory<byte> data) where T : class
    {
        return JsonSerializer.Deserialize<T>(data.Span, _options);
    }
}
