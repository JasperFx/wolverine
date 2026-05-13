using System.Text.Json;
using JasperFx.Core;
using Wolverine.Runtime.Serialization;

namespace Wolverine;

public sealed partial class WolverineOptions
{
    private readonly IDictionary<string, IMessageSerializer>
        _serializers = new Dictionary<string, IMessageSerializer>();

    private IMessageSerializer? _defaultSerializer;

    /// <summary>
    ///     Override or get the default message serializer for the application. The default is
    ///     <see cref="SystemTextJsonSerializer"/> (wired in the <see cref="WolverineOptions"/>
    ///     constructor via <see cref="UseSystemTextJsonForSerialization"/>). To restore the
    ///     5.x-and-earlier Newtonsoft.Json default, install the
    ///     <c>WolverineFx.Newtonsoft</c> package and call its
    ///     <c>UseNewtonsoftForSerialization()</c> extension method. As of Wolverine 6.0
    ///     the Newtonsoft surface lives in a separate NuGet package; see the
    ///     migration guide.
    /// </summary>
    /// <exception cref="InvalidOperationException"></exception>
    public IMessageSerializer DefaultSerializer
    {
        get
        {
            return _defaultSerializer ??=
                _serializers.Values.FirstOrDefault(x => x.ContentType == EnvelopeConstants.JsonContentType) ??
                _serializers.Values.First();
        }
        set
        {
            if (value == null)
            {
                throw new InvalidOperationException("The DefaultSerializer cannot be null");
            }

            _serializers[value.ContentType] = value;
            _defaultSerializer = value;
        }
    }

    public IMessageSerializer DetermineSerializer(Envelope envelope)
    {
        if (envelope.ContentType.IsEmpty())
        {
            return DefaultSerializer;
        }

        if (_serializers.TryGetValue(envelope.ContentType, out var serializer))
        {
            return serializer;
        }

        return DefaultSerializer;
    }

    /// <summary>
    ///     Use System.Text.Json as the default JSON serialization with optional configuration
    /// </summary>
    /// <param name="configuration"></param>
    public void UseSystemTextJsonForSerialization(Action<JsonSerializerOptions>? configuration = null)
    {
        var options = SystemTextJsonSerializer.DefaultOptions();

        configuration?.Invoke(options);

        var serializer = new SystemTextJsonSerializer(options);

        if (_defaultSerializer?.ContentType == "application/json")
        {
            _defaultSerializer = serializer;
        }
        else
        {
            _defaultSerializer ??= serializer;
        }

        _serializers[serializer.ContentType] = serializer;
    }

    internal IMessageSerializer FindSerializer(string contentType)
    {
        if (_serializers.TryGetValue(contentType, out var serializer))
        {
            return serializer;
        }

        throw new ArgumentOutOfRangeException(nameof(contentType));
    }

    /// <summary>
    /// Try to resolve a previously-registered serializer by its content-type.
    /// Returns null when no serializer is registered under the given content-type.
    /// </summary>
    internal IMessageSerializer? TryFindSerializer(string contentType)
    {
        if (_serializers.TryGetValue(contentType, out var s))
        {
            return s;
        }

        return null;
    }

    /// <summary>
    ///     Register an alternative serializer with this Wolverine application
    /// </summary>
    /// <param name="serializer"></param>
    public void AddSerializer(IMessageSerializer serializer)
    {
        _serializers[serializer.ContentType] = serializer;
    }
}
