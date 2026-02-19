using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wolverine.Configuration.Capabilities;

/// <summary>
/// System.Text.Json has no built-in converter for System.Version.
/// Without this, Version serializes as an empty object and deserializes as null.
/// </summary>
public class VersionJsonConverter : JsonConverter<Version>
{
    public override Version? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var s = reader.GetString();
        return s is null ? null : Version.Parse(s);
    }

    public override void Write(Utf8JsonWriter writer, Version value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToString());
    }
}
