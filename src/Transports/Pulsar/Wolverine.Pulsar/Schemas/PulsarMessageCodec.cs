using System.Buffers;
using DotPulsar;
using DotPulsar.Abstractions;

namespace Wolverine.Pulsar.Schemas;

/// <summary>
/// Owns the on-the-wire encoding of a Pulsar message body for a schema where the schema itself defines the
/// encoding (e.g. Avro), as opposed to the JSON pass-through schema where Wolverine owns the body bytes
/// (GH-3213). The sender encodes <c>envelope.Message</c> directly and the listener decodes back to the
/// message object (set on <c>envelope.Message</c>, bypassing Wolverine's body deserialization), while the
/// matching pass-through <see cref="PulsarSchema"/> still registers the schema with the broker.
/// </summary>
internal interface IPulsarMessageCodec
{
    SchemaInfo SchemaInfo { get; }

    /// <summary>The CLR message type this codec encodes/decodes.</summary>
    Type MessageType { get; }

    ReadOnlySequence<byte> Encode(object message);

    object Decode(ReadOnlySequence<byte> data);
}

/// <summary>
/// Avro codec backed by DotPulsar's built-in <see cref="Schema.AvroISpecificRecord{T}"/> (GH-3213).
/// <typeparamref name="T"/> must be an Apache.Avro <c>ISpecificRecord</c> at runtime — DotPulsar resolves
/// the Avro schema from the type's generated schema. The body bytes on the wire are genuine Avro.
/// </summary>
internal sealed class PulsarAvroCodec<T> : IPulsarMessageCodec where T : class
{
    private readonly ISchema<T> _schema = Schema.AvroISpecificRecord<T>();

    public SchemaInfo SchemaInfo => _schema.SchemaInfo;

    public Type MessageType => typeof(T);

    public ReadOnlySequence<byte> Encode(object message)
    {
        return _schema.Encode((T)message);
    }

    public object Decode(ReadOnlySequence<byte> data)
    {
        return _schema.Decode(data)!;
    }
}
