using System.Buffers;
using System.Text.Json;
using System.Text.Json.Schema;
using Dekaf.SchemaRegistry;
using Dekaf.Serialization;

namespace Wolverine.Kafka.Serialization;

/// <summary>
/// Schema Registry–aware Wolverine serializer for JSON Schema (Confluent wire format). The JSON
/// schema for each message type is generated from the type itself via
/// <see cref="JsonSchemaExporter"/> and registered/looked up per the configured subject naming.
/// </summary>
public sealed class SchemaRegistryJsonSerializer : SchemaRegistrySerializer
{
    private readonly ISchemaRegistryClient _schemaRegistry;
    private readonly JsonSchemaSerializerConfig? _serializerConfig;
    private readonly JsonSerializerOptions _jsonOptions;

    public SchemaRegistryJsonSerializer(
        ISchemaRegistryClient schemaRegistry,
        JsonSchemaSerializerConfig? serializerConfig = null,
        JsonSerializerOptions? jsonOptions = null)
    {
        _schemaRegistry = schemaRegistry ?? throw new ArgumentNullException(nameof(schemaRegistry));
        _serializerConfig = serializerConfig;
        _jsonOptions = jsonOptions ?? JsonSerializerOptions.Default;
    }

    public override string ContentType => "application/json";

    protected override Func<object, string?, byte[]> CreateTypedSerializer<T>()
    {
        var schema = _jsonOptions.GetJsonSchemaAsNode(typeof(T)).ToJsonString();
        var serializer = new JsonSchemaRegistrySerializer<T>(_schemaRegistry, schema, _jsonOptions,
            _serializerConfig ?? new JsonSchemaSerializerConfig(), ownsClient: false);

        return (message, topicName) =>
        {
            var context = new SerializationContext
            {
                Component = SerializationComponent.Value,
                Topic = topicName ?? string.Empty
            };

            // Schema registration/lookup is asynchronous but cached, so this completes synchronously
            // on every call after the first per message type. Only the first-contact registry round
            // trip blocks, forced by Wolverine's synchronous IMessageSerializer contract.
            var prepare = serializer.PrepareAsync((T)message, context, CancellationToken.None);
#pragma warning disable VSTHRD002 // Avoid problematic synchronous waits
            if (prepare.IsCompletedSuccessfully)
            {
                prepare.GetAwaiter().GetResult();
            }
            else
            {
                prepare.AsTask().GetAwaiter().GetResult();
            }
#pragma warning restore VSTHRD002 // Avoid problematic synchronous waits

            var buffer = new ArrayBufferWriter<byte>();
            serializer.Serialize((T)message, ref buffer, context);
            return buffer.WrittenSpan.ToArray();
        };
    }

    protected override Func<byte[], string?, object> CreateTypedDeserializer<T>()
    {
        var deserializer = new JsonSchemaRegistryDeserializer<T>(_schemaRegistry, _jsonOptions,
            ownsClient: false, ruleExecutor: null!);

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
