using System.Buffers;
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

    protected override Func<object, string?, byte[]> CreateTypedSerializer<T>()
    {
        var serializer = new AvroSchemaRegistrySerializer<T>(_schemaRegistry,
            _serializerConfig ?? new AvroSerializerConfig(), ownsClient: false);

        return (message, topicName) =>
        {
            var context = new SerializationContext
            {
                Component = SerializationComponent.Value,
                Topic = topicName ?? string.Empty
            };

#pragma warning disable VSTHRD002 // Avoid problematic synchronous waits
            // Schema registration/lookup is asynchronous; the actual serialization is synchronous
            serializer.PrepareAsync((T)message, context, CancellationToken.None)
                .AsTask().GetAwaiter().GetResult();
#pragma warning restore VSTHRD002 // Avoid problematic synchronous waits

            var buffer = new ArrayBufferWriter<byte>();
            serializer.Serialize((T)message, ref buffer, context);
            return buffer.WrittenSpan.ToArray();
        };
    }

    protected override Func<byte[], string?, object> CreateTypedDeserializer<T>()
    {
        var deserializer = new AvroSchemaRegistryDeserializer<T>(_schemaRegistry,
            new AvroDeserializerConfig(), ownsClient: false);

        return (data, topicName) =>
        {
            var context = new SerializationContext
            {
                Component = SerializationComponent.Value,
                Topic = topicName ?? string.Empty
            };

            var memory = new ReadOnlyMemory<byte>(data);

            // Schema resolution against the registry is asynchronous on first use per schema id
            if (((IAsyncDeserializerPreparer<T>)deserializer).TryDeserialize(memory, context, out var value))
            {
                return value!;
            }

#pragma warning disable VSTHRD002 // Avoid problematic synchronous waits
            ((IAsyncDeserializerPreparer<T>)deserializer)
                .PrepareAsync(memory, context, CancellationToken.None)
                .AsTask().GetAwaiter().GetResult();
#pragma warning restore VSTHRD002 // Avoid problematic synchronous waits

            return deserializer.Deserialize(memory, context)!;
        };
    }
}
