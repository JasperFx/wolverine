using System.Buffers;
using System.Reflection;
using Dekaf.Serialization;
using ImTools;
using Wolverine.Runtime.Serialization;

namespace Wolverine.Kafka.Serialization;

/// <summary>
/// Abstract base for Wolverine serializers that delegate to Confluent Schema Registry–aware
/// serde pairs (e.g. JSON, Avro, Protobuf).
/// <para>
/// Subclasses only provide <see cref="ContentType"/> and the two typed factory methods
/// <see cref="CreateTypedSerializer{T}"/> / <see cref="CreateTypedDeserializer{T}"/> returning
/// the Dekaf serdes. All caching, reflection bridging, and the
/// <see cref="IMessageSerializer"/> / <see cref="IAsyncMessageSerializer"/> plumbing live here.
/// </para>
/// <para>
/// The async paths await schema registration/lookup against the registry. Wolverine's send and
/// outbox paths serialize through them before anything reads <see cref="Envelope.Data"/>, and
/// receiving deserializes through them. The synchronous fallbacks share the same serde instances
/// and remain for callers of the synchronous <see cref="IMessageSerializer"/> contract (reading
/// dead letters, for example); they only block on first contact with a message type or schema id
/// that no async call has prepared yet.
/// </para>
/// </summary>
public abstract class SchemaRegistrySerializer : IAsyncMessageSerializer
{
    // Larger write buffers are not kept for reuse, so one big message does not pin its buffer to the thread
    private const int MaxRetainedBufferSize = 1024 * 1024;

    [ThreadStatic] private static ArrayBufferWriter<byte>? _writeBuffer;

    private ImHashMap<Type, ITypedSerializer> _serializers = ImHashMap<Type, ITypedSerializer>.Empty;
    private ImHashMap<Type, ITypedDeserializer> _deserializers = ImHashMap<Type, ITypedDeserializer>.Empty;

    private readonly MethodInfo _buildSerializerMethod;
    private readonly MethodInfo _buildDeserializerMethod;

    protected SchemaRegistrySerializer()
    {
        _buildSerializerMethod = typeof(SchemaRegistrySerializer)
            .GetMethod(nameof(buildSerializer), BindingFlags.NonPublic | BindingFlags.Instance)!;

        _buildDeserializerMethod = typeof(SchemaRegistrySerializer)
            .GetMethod(nameof(buildDeserializer), BindingFlags.NonPublic | BindingFlags.Instance)!;
    }

    public abstract string ContentType { get; }

    public byte[] Write(Envelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope.Message);

        return serializerFor(envelope.Message.GetType()).Serialize(envelope.Message, envelope.TopicName);
    }

    public ValueTask<byte[]> WriteAsync(Envelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope.Message);

        return serializerFor(envelope.Message.GetType()).SerializeAsync(envelope.Message, envelope.TopicName);
    }

    public byte[] WriteMessage(object message)
    {
        return serializerFor(message.GetType()).Serialize(message, null);
    }

    public object ReadFromData(Type messageType, Envelope envelope)
    {
        var data = envelope.Data ?? throw new InvalidOperationException("Envelope has no data.");

        return deserializerFor(messageType).Deserialize(data, envelope.TopicName);
    }

    public async ValueTask<object?> ReadFromDataAsync(Type messageType, Envelope envelope)
    {
        var data = envelope.Data ?? throw new InvalidOperationException("Envelope has no data.");

        return await deserializerFor(messageType).DeserializeAsync(data, envelope.TopicName);
    }

    public object ReadFromData(byte[] data)
    {
        throw new NotSupportedException(
            $"{GetType().Name} requires a known message type. Use ReadFromData(Type, Envelope) instead.");
    }

    /// <summary>
    /// Creates the Dekaf serializer for <typeparamref name="T"/>. Called once per message type,
    /// then cached and shared by the sync and async write paths.
    /// </summary>
    protected abstract ISerializer<T> CreateTypedSerializer<T>() where T : class;

    /// <summary>
    /// Creates the Dekaf deserializer for <typeparamref name="T"/>. Called once per message type,
    /// then cached and shared by the sync and async read paths.
    /// </summary>
    protected abstract IDeserializer<T> CreateTypedDeserializer<T>() where T : class;

    private ITypedSerializer serializerFor(Type messageType)
    {
        if (_serializers.TryFind(messageType, out var serializer)) return serializer;

        serializer = (ITypedSerializer)_buildSerializerMethod.MakeGenericMethod(messageType).Invoke(this, null)!;
        _serializers = _serializers.AddOrUpdate(messageType, serializer);
        return serializer;
    }

    private ITypedDeserializer deserializerFor(Type messageType)
    {
        if (_deserializers.TryFind(messageType, out var deserializer)) return deserializer;

        deserializer = (ITypedDeserializer)_buildDeserializerMethod.MakeGenericMethod(messageType).Invoke(this, null)!;
        _deserializers = _deserializers.AddOrUpdate(messageType, deserializer);
        return deserializer;
    }

    private ITypedSerializer buildSerializer<T>() where T : class
    {
        return new TypedSerializer<T>(CreateTypedSerializer<T>());
    }

    private ITypedDeserializer buildDeserializer<T>() where T : class
    {
        return new TypedDeserializer<T>(CreateTypedDeserializer<T>());
    }

    // Only the synchronous fallbacks block, and only until the schema for a message type or schema id is cached
    private static void waitFor(ValueTask prepare)
    {
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
    }

    private static SerializationContext contextFor(string? topicName)
    {
        return new SerializationContext
        {
            Component = SerializationComponent.Value,
            Topic = topicName ?? string.Empty
        };
    }

    private interface ITypedSerializer
    {
        byte[] Serialize(object message, string? topicName);
        ValueTask<byte[]> SerializeAsync(object message, string? topicName);
    }

    private interface ITypedDeserializer
    {
        object Deserialize(byte[] data, string? topicName);
        ValueTask<object> DeserializeAsync(byte[] data, string? topicName);
    }

    private sealed class TypedSerializer<T> : ITypedSerializer where T : class
    {
        private readonly ISerializer<T> _serializer;
        private readonly IAsyncSerializerPreparer<T>? _preparer;

        public TypedSerializer(ISerializer<T> serializer)
        {
            _serializer = serializer;
            _preparer = serializer as IAsyncSerializerPreparer<T>;
        }

        public byte[] Serialize(object message, string? topicName)
        {
            var context = contextFor(topicName);
            if (_preparer != null)
            {
                // Schema registration/lookup is cached, so this completes synchronously for any
                // type a previous sync or async call has already prepared
                waitFor(_preparer.PrepareAsync((T)message, context, CancellationToken.None));
            }

            return write((T)message, context);
        }

        public async ValueTask<byte[]> SerializeAsync(object message, string? topicName)
        {
            var context = contextFor(topicName);
            if (_preparer != null)
            {
                await _preparer.PrepareAsync((T)message, context, CancellationToken.None);
            }

            return write((T)message, context);
        }

        private byte[] write(T message, SerializationContext context)
        {
            var buffer = _writeBuffer ?? new ArrayBufferWriter<byte>();
            _writeBuffer = null;
            buffer.ResetWrittenCount();

            _serializer.Serialize(message, ref buffer, context);
            var bytes = buffer.WrittenSpan.ToArray();

            if (buffer.Capacity <= MaxRetainedBufferSize)
            {
                _writeBuffer = buffer;
            }

            return bytes;
        }
    }

    private sealed class TypedDeserializer<T> : ITypedDeserializer where T : class
    {
        private readonly IDeserializer<T> _deserializer;
        private readonly IAsyncDeserializerPreparer<T>? _preparer;

        public TypedDeserializer(IDeserializer<T> deserializer)
        {
            _deserializer = deserializer;
            _preparer = deserializer as IAsyncDeserializerPreparer<T>;
        }

        public object Deserialize(byte[] data, string? topicName)
        {
            var context = contextFor(topicName);
            var memory = new ReadOnlyMemory<byte>(data);

            if (_preparer != null)
            {
                if (_preparer.TryDeserialize(memory, context, out var value))
                {
                    return value!;
                }

                // First sight of this schema id requires a registry fetch
                waitFor(_preparer.PrepareAsync(memory, context, CancellationToken.None));
            }

            return _deserializer.Deserialize(memory, context)!;
        }

        public async ValueTask<object> DeserializeAsync(byte[] data, string? topicName)
        {
            var context = contextFor(topicName);
            var memory = new ReadOnlyMemory<byte>(data);

            if (_preparer != null)
            {
                if (_preparer.TryDeserialize(memory, context, out var value))
                {
                    return value!;
                }

                await _preparer.PrepareAsync(memory, context, CancellationToken.None);
            }

            return _deserializer.Deserialize(memory, context)!;
        }
    }
}
