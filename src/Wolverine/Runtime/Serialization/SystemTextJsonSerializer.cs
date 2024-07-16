using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wolverine.Runtime.Serialization;

/// <summary>
///     Use System.Text.Json as the JSON serialization
/// </summary>
public class SystemTextJsonSerializer : IMessageSerializer
{
    private readonly JsonSerializerOptions _options;

    public SystemTextJsonSerializer(JsonSerializerOptions options)
    {
        _options = options;
        _options.Converters.Add(new CustomJsonConverterForType());
    }

    public string ContentType => EnvelopeConstants.JsonContentType;

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

    public static JsonSerializerOptions DefaultOptions()
    {
        return new JsonSerializerOptions
        {
            AllowTrailingCommas = true,
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            ReadCommentHandling = JsonCommentHandling.Skip,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
    }
}

internal class CustomJsonConverterForType : JsonConverter<Type>
{
    public override Type Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
        )
    {
        // Caution: Deserialization of type instances like this 
        // is not recommended and should be avoided
        // since it can lead to potential security issues.

        // If you really want this supported (for instance if the JSON input is trusted):
        // string assemblyQualifiedName = reader.GetString();
        // return Type.GetType(assemblyQualifiedName);
        throw new NotSupportedException();
    }

    public override void Write(
        Utf8JsonWriter writer,
        Type value,
        JsonSerializerOptions options
        )
    {
        string assemblyQualifiedName = value.AssemblyQualifiedName;
        // Use this with caution, since you are disclosing type information.
        writer.WriteStringValue(assemblyQualifiedName);
    }
}