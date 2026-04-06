using Confluent.Kafka;
using Confluent.SchemaRegistry;
using Confluent.SchemaRegistry.Serdes;

namespace Wolverine.Kafka.Serialization;

/// <summary>
/// Schema Registry–aware Wolverine serializer for Avro (Confluent wire format).
/// <para>
/// Message types must implement <see cref="Avro.Specific.ISpecificRecord"/>.
/// </para>
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

    protected override Func<object, string?, byte[]> CreateTypedSerializer<T>()
    {
        var serializer = new AvroSerializer<T>(_schemaRegistry, _serializerConfig);

        return (message, topicName) =>
        {
            var context = new SerializationContext(
                MessageComponentType.Value, topicName ?? string.Empty);

            return serializer.SerializeAsync((T)message, context)
                .GetAwaiter().GetResult();
        };
    }

    protected override Func<byte[], string?, object> CreateTypedDeserializer<T>()
    {
        var deserializer = new AvroDeserializer<T>(_schemaRegistry);

        return (data, topicName) =>
        {
            var context = new SerializationContext(
                MessageComponentType.Value, topicName ?? string.Empty);

            return deserializer.DeserializeAsync(
                new ReadOnlyMemory<byte>(data), isNull: false, context)
                .GetAwaiter().GetResult()!;
        };
    }
}
