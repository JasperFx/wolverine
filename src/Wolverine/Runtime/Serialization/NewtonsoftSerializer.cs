using System.Buffers;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Wolverine.Runtime.Serialization;

public class NewtonsoftSerializer : IMessageSerializer
{
    private readonly ArrayPool<byte> _bytePool;
    private readonly JsonArrayPool<char> _jsonCharPool;

    private readonly JsonSerializer _serializer;
    private int _bufferSize = 2048;

    public NewtonsoftSerializer(JsonSerializerSettings settings)
    {
        _serializer = JsonSerializer.Create(settings);

        _bytePool = ArrayPool<byte>.Shared;
        var charPool = ArrayPool<char>.Shared;
        _jsonCharPool = new JsonArrayPool<char>(charPool);

        Settings = settings;
    }

    public JsonSerializerSettings Settings { get; }

    public string ContentType => EnvelopeConstants.JsonContentType;

    public byte[] Write(Envelope envelope)
    {
        var message = envelope.Message!;
        return WriteMessage(message);
    }

    public object ReadFromData(Type messageType, Envelope envelope)
    {
        using var stream = new MemoryStream(envelope.Data!)
        {
            Position = 0
        };

        using var streamReader = new StreamReader(stream, Encoding.UTF8, true, _bufferSize, true);
        using var jsonReader = new JsonTextReader(streamReader)
        {
            ArrayPool = _jsonCharPool,
            CloseInput = false
        };

        return _serializer.Deserialize(jsonReader, messageType)!;
    }

    public object ReadFromData(byte[] data)
    {
        using var stream = new MemoryStream(data)
        {
            Position = 0
        };

        using var streamReader = new StreamReader(stream, Encoding.UTF8, true, _bufferSize, true);
        using var jsonReader = new JsonTextReader(streamReader)
        {
            ArrayPool = _jsonCharPool,
            CloseInput = false
        };

        var message = _serializer.Deserialize(jsonReader)!;

        if (message is JObject)
        {
            throw new InvalidOperationException("Unable to determine the message type in deserialization");
        }

        return message;
    }
    
    public T ReadFromData<T>(byte[] data)
    {
        using var stream = new MemoryStream(data)
        {
            Position = 0
        };

        using var streamReader = new StreamReader(stream, Encoding.UTF8, true, _bufferSize, true);
        using var jsonReader = new JsonTextReader(streamReader)
        {
            ArrayPool = _jsonCharPool,
            CloseInput = false
        };

        return _serializer.Deserialize<T>(jsonReader)!;
    }

    public byte[] WriteMessage(object message)
    {
        var bytes = _bytePool.Rent(_bufferSize);
        var stream = new MemoryStream(bytes);


        try
        {
            using var textWriter = new StreamWriter(stream) { AutoFlush = true };
            using var jsonWriter = new JsonTextWriter(textWriter)
            {
                ArrayPool = _jsonCharPool,
                CloseOutput = false,

                AutoCompleteOnClose = false
            };

            _serializer.Serialize(jsonWriter, message);
            return stream.Position < _bufferSize
                ? bytes[..(int)stream.Position]
                : stream.ToArray();
        }

        catch (NotSupportedException e)
        {
            if (e.Message.Contains("Memory stream is not expandable"))
            {
                var data = writeWithNoBuffer(message, _serializer);

                var bufferSize = 1024;
                while (bufferSize < data.Length)
                {
                    bufferSize = bufferSize * 2;
                }

                _bufferSize = bufferSize;

                return data;
            }

            throw;
        }

        finally
        {
            _bytePool.Return(bytes);
        }
    }

    public static JsonSerializerSettings DefaultSettings()
    {
        #region sample_default_newtonsoft_settings

        return new JsonSerializerSettings
        {
            TypeNameHandling = TypeNameHandling.Auto,
            PreserveReferencesHandling = PreserveReferencesHandling.Objects
        };

        #endregion
    }

    private byte[] writeWithNoBuffer(object? model, JsonSerializer serializer)
    {
        var stream = new MemoryStream();
        using var textWriter = new StreamWriter(stream) { AutoFlush = true };
        using var jsonWriter = new JsonTextWriter(textWriter)
        {
            ArrayPool = _jsonCharPool,
            CloseOutput = false,

            AutoCompleteOnClose = false
        };

        serializer.Serialize(jsonWriter, model);
        return stream.ToArray();
    }
}