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

    protected override ISerializer<T> CreateTypedSerializer<T>()
    {
        var schema = _jsonOptions.GetJsonSchemaAsNode(typeof(T)).ToJsonString();
        return new JsonSchemaRegistrySerializer<T>(_schemaRegistry, schema, _jsonOptions,
            _serializerConfig ?? new JsonSchemaSerializerConfig(), ownsClient: false);
    }

    protected override IDeserializer<T> CreateTypedDeserializer<T>()
    {
        return new JsonSchemaRegistryDeserializer<T>(_schemaRegistry, _jsonOptions,
            ownsClient: false, ruleExecutor: null!);
    }
}
