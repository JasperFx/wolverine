using Confluent.Kafka;
using Confluent.SchemaRegistry;
using Confluent.SchemaRegistry.Serdes;

namespace Wolverine.Kafka.Serialization;

/// <summary>
/// Schema Registry–aware Wolverine serializer for JSON (Confluent wire format).
/// </summary>
public sealed class SchemaRegistryJsonSerializer : SchemaRegistrySerializer
{
    private readonly ISchemaRegistryClient _schemaRegistry;
    private readonly JsonSerializerConfig? _serializerConfig;

    public SchemaRegistryJsonSerializer(
        ISchemaRegistryClient schemaRegistry,
        JsonSerializerConfig? serializerConfig = null)
    {
        _schemaRegistry = schemaRegistry ?? throw new ArgumentNullException(nameof(schemaRegistry));
        _serializerConfig = serializerConfig;
    }

    public override string ContentType => "application/json";

    protected override Func<object, string?, byte[]> CreateTypedSerializer<T>()
    {
        var serializer = new JsonSerializer<T>(_schemaRegistry, _serializerConfig);

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
        var deserializer = new JsonDeserializer<T>(_schemaRegistry);

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
