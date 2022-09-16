using System;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Wolverine.Runtime.Serialization;

/// <summary>
///     Use System.Text.Json as the JSON serialization
/// </summary>
public class SystemTextJsonSerializer : IMessageSerializer
{
    public static JsonSerializerOptions DefaultOptions()
    {
        return new JsonSerializerOptions
        {
            AllowTrailingCommas = true,
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            ReadCommentHandling = JsonCommentHandling.Skip,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
    }

    private readonly JsonSerializerOptions _options;

    public SystemTextJsonSerializer(JsonSerializerOptions options)
    {
        _options = options;
    }

    public string ContentType { get; } = EnvelopeConstants.JsonContentType;

    public byte[] Write(Envelope envelope)
    {
        return JsonSerializer.SerializeToUtf8Bytes(envelope.Message, _options);
    }

    public object ReadFromData(Type messageType, Envelope envelope)
    {
        return JsonSerializer.Deserialize(envelope.Data, messageType, _options)!;
    }

    public object ReadFromData(byte[]? data)
    {
        throw new NotSupportedException("System.Text.Json requires a known message type");
    }

    public byte[] WriteMessage(object message)
    {
        return JsonSerializer.SerializeToUtf8Bytes(message, _options);
    }
}
