using System.Collections.Concurrent;
using System.Reflection;
using Wolverine.Runtime.Serialization;

namespace Wolverine.Kafka.Serialization;

/// <summary>
/// Abstract base for Wolverine <see cref="IMessageSerializer"/> implementations that
/// delegate to Confluent Schema Registry–aware serde pairs (e.g. JSON, Avro, Protobuf).
/// <para>
/// Subclasses only provide <see cref="ContentType"/> and the two typed factory methods
/// <see cref="CreateTypedSerializer{T}"/> / <see cref="CreateTypedDeserializer{T}"/>.
/// All caching, reflection bridging, and <see cref="IMessageSerializer"/> plumbing live here.
/// </para>
/// </summary>
public abstract class SchemaRegistrySerializer : IMessageSerializer
{
    private readonly ConcurrentDictionary<Type, Func<object, string?, byte[]>> _serializerCache = new();
    private readonly ConcurrentDictionary<Type, Func<byte[], string?, object>> _deserializerCache = new();

    private readonly MethodInfo _createTypedSerializerMethod;
    private readonly MethodInfo _createTypedDeserializerMethod;

    protected SchemaRegistrySerializer()
    {
        var type = GetType();

        _createTypedSerializerMethod = type
            .GetMethod(nameof(CreateTypedSerializer), BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new MissingMethodException(type.Name, nameof(CreateTypedSerializer));

        _createTypedDeserializerMethod = type
            .GetMethod(nameof(CreateTypedDeserializer), BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new MissingMethodException(type.Name, nameof(CreateTypedDeserializer));
    }

    public abstract string ContentType { get; }

    public byte[] Write(Envelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope.Message);

        var serialize = GetOrCreateSerializer(envelope.Message.GetType());
        return serialize(envelope.Message, envelope.TopicName);
    }

    public byte[] WriteMessage(object message)
    {
        var serialize = GetOrCreateSerializer(message.GetType());
        return serialize(message, null);
    }

    public object ReadFromData(Type messageType, Envelope envelope)
    {
        var data = envelope.Data ?? throw new InvalidOperationException("Envelope has no data.");

        var deserialize = _deserializerCache.GetOrAdd(messageType, type =>
            (Func<byte[], string?, object>)_createTypedDeserializerMethod
                .MakeGenericMethod(type)
                .Invoke(this, null)!);

        return deserialize(data, envelope.TopicName);
    }

    public object ReadFromData(byte[] data)
    {
        throw new NotSupportedException(
            $"{GetType().Name} requires a known message type. Use ReadFromData(Type, Envelope) instead.");
    }

    /// <summary>
    /// Creates a delegate that serializes <typeparamref name="T"/> into Confluent wire-format
    /// bytes using the concrete serde. Called once per message type, then cached.
    /// </summary>
    protected abstract Func<object, string?, byte[]> CreateTypedSerializer<T>() where T : class;

    /// <summary>
    /// Creates a delegate that deserializes Confluent wire-format bytes into
    /// <typeparamref name="T"/> using the concrete serde. Called once per message type, then cached.
    /// </summary>
    protected abstract Func<byte[], string?, object> CreateTypedDeserializer<T>() where T : class;

    private Func<object, string?, byte[]> GetOrCreateSerializer(Type messageType)
    {
        return _serializerCache.GetOrAdd(messageType, type =>
            (Func<object, string?, byte[]>)_createTypedSerializerMethod
                .MakeGenericMethod(type)
                .Invoke(this, null)!);
    }
}
