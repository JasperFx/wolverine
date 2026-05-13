using System.Text.Json;
using ImTools;
using JasperFx.Core;
using Wolverine.Runtime.Serialization;

namespace Wolverine.Runtime.Interop.MassTransit;

public class MassTransitJsonSerializer : IMessageSerializer, IMassTransitInterop
{
    private readonly string? _destination;
    private readonly IMassTransitInteropEndpoint _endpoint;

    private readonly Lazy<string> _reply;

    private IMessageSerializer
        _inner = new SystemTextJsonSerializer(SystemTextJsonSerializer.DefaultOptions());

    private ImHashMap<string, Uri?> _uriMap = ImHashMap<string, Uri?>.Empty;

    public MassTransitJsonSerializer(IMassTransitInteropEndpoint endpoint)
    {
        _endpoint = endpoint;
        _destination = endpoint.MassTransitUri()?.ToString();
        _reply = new Lazy<string>(() => endpoint.MassTransitReplyUri()?.ToString() ?? string.Empty);
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
    ///     Hook used by the WolverineFx.Newtonsoft package's
    ///     <c>UseNewtonsoftForSerialization(IMassTransitInterop)</c> extension
    ///     method to swap the inner JSON serializer for a Newtonsoft.Json one
    ///     when wire-compatibility with MassTransit producers / consumers is
    ///     required. Internal so the public surface only acknowledges the
    ///     STJ default; Newtonsoft is opt-in via the separate NuGet package.
    /// </summary>
    /// <param name="serializer">
    ///     The serializer to use for the inner JSON layer wrapped by the
    ///     <c>application/vnd.masstransit+json</c> envelope.
    /// </param>
    internal void ApplyInnerSerializer(IMessageSerializer serializer)
    {
        _inner = serializer ?? throw new ArgumentNullException(nameof(serializer));
    }

    public string ContentType => "application/vnd.masstransit+json";

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