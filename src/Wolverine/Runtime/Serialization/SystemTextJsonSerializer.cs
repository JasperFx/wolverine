using System.Diagnostics.CodeAnalysis;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wolverine.Runtime.Serialization;

/// <summary>
///     Use System.Text.Json as the JSON serialization.
/// </summary>
/// <remarks>
/// Wolverine's default serializer. Calls the reflection-based
/// <see cref="JsonSerializer"/> overloads (no <c>JsonTypeInfo</c> /
/// <c>JsonSerializerContext</c>), so the <c>Write</c> / <c>ReadFromData</c>
/// methods carry <c>[RequiresUnreferencedCode]</c> + <c>[RequiresDynamicCode]</c>.
/// AOT-clean apps that want STJ should supply a custom <see cref="IMessageSerializer"/>
/// implementation wrapping <c>JsonSerializer.Serialize&lt;T&gt;(value, JsonTypeInfo)</c>
/// — see the Wolverine AOT publishing guide.
/// </remarks>
public class SystemTextJsonSerializer : IMessageSerializer
{
    private readonly JsonSerializerOptions _options;

    public SystemTextJsonSerializer(JsonSerializerOptions options)
    {
        _options = options;
        _options.Converters.Add(new CustomJsonConverterForType());
    }

    public string ContentType => EnvelopeConstants.JsonContentType;

    // SystemTextJsonSerializer is Wolverine's default serializer. By design it
    // calls the reflection-based JsonSerializer overloads (no JsonTypeInfo /
    // JsonSerializerContext). The trim warnings on those calls are expected;
    // suppressing at the leaf rather than annotating the IMessageSerializer
    // interface keeps the cascade contained and matches how upstream STJ
    // documents the dynamic-code escape valve for default-options serialization.
    //
    // AOT-clean apps that want STJ should supply their own IMessageSerializer
    // implementation that wraps JsonSerializer.Serialize<T>(value, JsonTypeInfo)
    // or JsonSerializer.Serialize(value, JsonSerializerContext). See the
    // Wolverine AOT publishing guide.
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "Default JSON serializer; AOT consumers wrap JsonSerializer with JsonTypeInfo. See AOT guide.")]
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "Default JSON serializer; AOT consumers wrap JsonSerializer with JsonTypeInfo. See AOT guide.")]
    public byte[] Write(Envelope envelope)
    {
        return JsonSerializer.SerializeToUtf8Bytes(envelope.Message, _options);
    }

    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "Default JSON serializer; AOT consumers wrap JsonSerializer with JsonTypeInfo. See AOT guide.")]
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "Default JSON serializer; AOT consumers wrap JsonSerializer with JsonTypeInfo. See AOT guide.")]
    public object ReadFromData(Type messageType, Envelope envelope)
    {
        return JsonSerializer.Deserialize(envelope.Data, messageType, _options)!;
    }

    public object ReadFromData(byte[]? data)
    {
        throw new NotSupportedException("System.Text.Json requires a known message type");
    }

    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "Default JSON serializer; AOT consumers wrap JsonSerializer with JsonTypeInfo. See AOT guide.")]
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "Default JSON serializer; AOT consumers wrap JsonSerializer with JsonTypeInfo. See AOT guide.")]
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
        string assemblyQualifiedName = value.AssemblyQualifiedName!;
        // Use this with caution, since you are disclosing type information.
        writer.WriteStringValue(assemblyQualifiedName);
    }
}