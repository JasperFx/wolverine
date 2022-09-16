using System;
using System.Text.Json;
using Baseline.ImTools;
using Wolverine.Util;
using Newtonsoft.Json;
using Wolverine.Runtime.Serialization;

namespace Wolverine.Runtime.Interop.MassTransit;

public interface IMassTransitInterop
{
    /// <summary>
    ///     Use System.Text.Json as the default JSON serialization with optional configuration
    /// </summary>
    /// <param name="configuration"></param>
    void UseSystemTextJsonForSerialization(Action<JsonSerializerOptions>? configuration = null);

    /// <summary>
    ///     Use Newtonsoft.Json as the default JSON serialization with optional configuration
    /// </summary>
    /// <param name="configuration"></param>
    void UseNewtonsoftForSerialization(Action<JsonSerializerSettings>? configuration = null);
}

public class MassTransitJsonSerializer : IMessageSerializer, IMassTransitInterop
{
    private readonly string? _destination;
    private readonly IMassTransitInteropEndpoint _endpoint;

    private IMessageSerializer
        _inner = new SystemTextJsonSerializer(SystemTextJsonSerializer.DefaultOptions());

    private readonly Lazy<string> _reply;

    private ImHashMap<string, Uri?> _uriMap = ImHashMap<string, Uri?>.Empty;

    public MassTransitJsonSerializer(IMassTransitInteropEndpoint endpoint)
    {
        _endpoint = endpoint;
        _destination = endpoint.MassTransitUri()?.ToString();
        _reply = new Lazy<string>(() => endpoint.MassTransitReplyUri()?.ToString());
    }

    /// <summary>
    ///     Use System.Text.Json as the default JSON serialization with optional configuration
    /// </summary>
    /// <param name="configuration"></param>
    public void UseSystemTextJsonForSerialization(Action<JsonSerializerOptions>? configuration = null)
    {
        var options = SystemTextJsonSerializer.DefaultOptions();

        configuration?.Invoke(options);

        _inner = new SystemTextJsonSerializer(options);
    }

    /// <summary>
    ///     Use Newtonsoft.Json as the default JSON serialization with optional configuration
    /// </summary>
    /// <param name="configuration"></param>
    public void UseNewtonsoftForSerialization(Action<JsonSerializerSettings>? configuration = null)
    {
        var settings = NewtonsoftSerializer.DefaultSettings();

        configuration?.Invoke(settings);

        var serializer = new NewtonsoftSerializer(settings);

        _inner = serializer;
    }

    public string ContentType { get; } = "application/vnd.masstransit+json";

    public byte[] Write(Envelope envelope)
    {
        var message = new MassTransitEnvelope(envelope)
        {
            DestinationAddress = _destination,
            ResponseAddress = _reply.Value
        };

        return _inner.WriteMessage(message);
    }

    public object ReadFromData(Type messageType, Envelope envelope)
    {
        var wrappedType = typeof(MassTransitEnvelope<>).MakeGenericType(messageType);

        var mtEnvelope = (IMassTransitEnvelope)_inner.ReadFromData(wrappedType, envelope);
        mtEnvelope.TransferData(envelope);
        envelope.ReplyUri = mapResponseUri(mtEnvelope.ResponseAddress ?? mtEnvelope.SourceAddress);

        return mtEnvelope.Body!;
    }

    public object ReadFromData(byte[] data)
    {
        // TODO -- IS there a default message type we could use?
        throw new NotSupportedException();
    }

    public byte[] WriteMessage(object message)
    {
        throw new NotSupportedException();
    }

    private Uri? mapResponseUri(string? responseAddress)
    {
        if (responseAddress == null)
        {
            return null;
        }

        if (_uriMap.TryFind(responseAddress, out var uri))
        {
            return uri;
        }

        var rabbitUri = responseAddress.ToUri();
        uri = _endpoint.TranslateMassTransitToWolverineUri(rabbitUri);
        _uriMap = _uriMap.AddOrUpdate(responseAddress, uri);
        return uri;
    }
}
