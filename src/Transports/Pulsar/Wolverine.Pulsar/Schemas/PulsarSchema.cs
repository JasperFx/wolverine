using System.Buffers;
using DotPulsar;
using DotPulsar.Abstractions;

namespace Wolverine.Pulsar.Schemas;

/// <summary>
/// A Pulsar schema that registers schema metadata with the broker (so the topic gets broker-side schema
/// registration, compatibility checks, and evolution) while leaving the message body bytes owned by
/// Wolverine's own serialization (GH-3183). <see cref="Encode"/> / <see cref="Decode"/> are pass-through:
/// the body is whatever Wolverine already serialized (e.g. JSON via <c>System.Text.Json</c>), and the
/// <see cref="SchemaInfo"/> declares the schema type + definition the broker stores for the topic.
///
/// This keeps the entire existing byte-oriented sender/listener/<see cref="PulsarEnvelopeMapper"/> and
/// CloudEvents pipeline intact — the only change is that the producer/consumer are created with this
/// schema so the broker registers it.
/// </summary>
internal sealed class PulsarSchema : ISchema<ReadOnlySequence<byte>>
{
    public PulsarSchema(SchemaInfo schemaInfo)
    {
        SchemaInfo = schemaInfo;
    }

    public SchemaInfo SchemaInfo { get; }

    public ReadOnlySequence<byte> Decode(ReadOnlySequence<byte> bytes, byte[]? schemaVersion = null)
    {
        return bytes;
    }

    public ReadOnlySequence<byte> Encode(ReadOnlySequence<byte> message)
    {
        return message;
    }

    /// <summary>
    /// Build a JSON-schema-typed Pulsar schema for the CLR message type <paramref name="messageType"/>.
    /// The schema definition is the Avro-format JSON schema Pulsar uses for <see cref="SchemaType.Json"/>,
    /// generated from the type's public properties. The body bytes remain Wolverine's JSON serialization.
    /// </summary>
    public static PulsarSchema ForJson(Type messageType)
    {
        var definition = AvroSchemaGenerator.Generate(messageType);
        var info = new SchemaInfo(messageType.Name, System.Text.Encoding.UTF8.GetBytes(definition),
            SchemaType.Json, new Dictionary<string, string>());
        return new PulsarSchema(info);
    }
}
