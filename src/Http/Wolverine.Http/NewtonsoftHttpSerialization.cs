using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;
using Lamar;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.ObjectPool;
using Newtonsoft.Json;

namespace Wolverine.Http;

[Singleton]
public class NewtonsoftHttpSerialization
{
    private readonly ArrayPool<byte> _bytePool;
    private readonly JsonArrayPool<char> _jsonCharPool;
    private readonly int _bufferSize = 1024;
    private readonly ArrayPool<char> _charPool;
    private readonly JsonSerializer _serializer;

    public NewtonsoftHttpSerialization(WolverineHttpOptions options)
    {
        _bytePool = ArrayPool<byte>.Shared;
        _charPool = ArrayPool<char>.Shared;
        _jsonCharPool = new JsonArrayPool<char>(_charPool);

        Settings = options.NewtonsoftSerializerSettings;
        _serializer = JsonSerializer.Create(Settings);
    }

    public JsonSerializerSettings Settings { get; set; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public async Task WriteJsonAsync(HttpContext context, object? body)
    {
        if (body == null)
        {
            context.Response.StatusCode = 404;
            return;
        }

        var response = context.Response;

        var responseStream = response.Body;

        await using var textWriter = new HttpResponseStreamWriter(responseStream, Encoding.Default, _bufferSize, _bytePool,
            ArrayPool<char>.Shared);
        using var jsonWriter = new JsonTextWriter(textWriter)
        {
            ArrayPool = _jsonCharPool,
            CloseOutput = false,
            AutoCompleteOnClose = false,
        };

        context.Response.ContentType = "application/json";

        _serializer.Serialize(jsonWriter, body);
        await jsonWriter.FlushAsync();
    }

    public async Task<T> ReadFromJsonAsync<T>(HttpContext context)
    {
        // TODO -- the encoding should vary here
        var targetType = typeof(T);

        var request = context.Request;

        if (!request.Body.CanSeek)
        {
            // JSON.Net does synchronous reads. In order to avoid blocking on the stream, we asynchronously
            // read everything into a buffer, and then seek back to the beginning.
            request.EnableBuffering();

            await request.Body.DrainAsync(CancellationToken.None);
            request.Body.Seek(0L, SeekOrigin.Begin);
        }

        using var streamReader =
            new HttpRequestStreamReader(request.Body, Encoding.UTF8, _bufferSize, _bytePool, _charPool);
        using var jsonReader = new JsonTextReader(streamReader);
        jsonReader.ArrayPool = _jsonCharPool;
        jsonReader.CloseInput = false;

        return (T)_serializer.Deserialize(jsonReader, targetType);
    }

    internal class JsonArrayPool<T> : IArrayPool<T>
    {
        private readonly ArrayPool<T> _inner;

        public JsonArrayPool(ArrayPool<T> inner)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        }

        public T[] Rent(int minimumLength)
        {
            return _inner.Rent(minimumLength);
        }

#pragma warning disable CS8767
        public void Return(T[] array)
#pragma warning restore CS8767
        {
            if (array == null)
            {
                throw new ArgumentNullException(nameof(array));
            }

            _inner.Return(array);
        }
    }
}