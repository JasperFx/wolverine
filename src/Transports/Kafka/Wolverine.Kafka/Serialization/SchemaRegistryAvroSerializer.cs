using Dekaf.SchemaRegistry;
using Dekaf.SchemaRegistry.Avro;
using Dekaf.Serialization;

namespace Wolverine.Kafka.Serialization;

/// <summary>
/// Schema Registry–aware Wolverine serializer for Avro (Confluent wire format).
/// </summary>
public sealed class SchemaRegistryAvroSerializer : SchemaRegistrySerializer
{
    private readonly ISchemaRegistryClient _schemaRegistry;
    private readonly AvroSerializerConfig? _serializerConfig;

    public SchemaRegistryAvroSerializer(
        ISchemaRegistryClient schemaRegistry,
        AvroSerializerConfig? serializerConfig = null)
    {
        _schemaRegistry = schemaRegistry ?? throw new ArgumentNullException(nameof(schemaRegistry));
        _serializerConfig = serializerConfig;
    }

    public override string ContentType => "application/avro";

    protected override ISerializer<T> CreateTypedSerializer<T>()
    {
        return new AvroSchemaRegistrySerializer<T>(_schemaRegistry,
            _serializerConfig ?? new AvroSerializerConfig(), ownsClient: false);
    }

    protected override IDeserializer<T> CreateTypedDeserializer<T>()
    {
        return new AvroSchemaRegistryDeserializer<T>(_schemaRegistry,
            new AvroDeserializerConfig(), ownsClient: false);
    }
}
