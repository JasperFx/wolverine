using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Wolverine.Persistence;
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

    private Func<MassTransitEnvelope, string?>? _tenantIdSource;

    private IClaimCheckStore? _messageDataStore;
    private Func<Uri, string>? _messageDataAddressToId;
    private Action<JsonSerializerOptions>? _jsonConfiguration;

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
        _jsonConfiguration = configuration;

        var options = SystemTextJsonSerializer.DefaultOptions();

        configuration?.Invoke(options);

        _inner = new SystemTextJsonSerializer(options);

        // GH-3510: re-attach the MessageData modifier if it was configured first, so the two opt-ins
        // compose regardless of the order the user calls them in.
        applyMessageDataOptions();
    }

    public IMassTransitInterop MapTenantIdFrom<T>(Func<MassTransitEnvelope<T>, string?> tenantIdSource)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(tenantIdSource);

        // Compose with any previously registered mapper so multiple message types can each
        // contribute their own tenant id extraction. A mapper only fires for its own T.
        var previous = _tenantIdSource;
        _tenantIdSource = mtEnvelope =>
            mtEnvelope is MassTransitEnvelope<T> typed ? tenantIdSource(typed) : previous?.Invoke(mtEnvelope);

        return this;
    }

    public IMassTransitInterop ReadMessageDataFrom(IClaimCheckStore store, Func<Uri, string>? addressToId = null)
    {
        ArgumentNullException.ThrowIfNull(store);

        _messageDataStore = store;
        _messageDataAddressToId = addressToId;

        // Rebuild the inner serializer so the modifier is attached. Any options the caller already applied
        // through UseSystemTextJsonForSerialization are re-applied on top, so call order does not matter.
        applyMessageDataOptions();

        return this;
    }

    // The MassTransit interop path is reflection-based JSON by construction -- it deserializes into
    // MassTransitEnvelope<T> closed over the runtime message type -- so it is already outside the
    // trim/AOT-safe subset that a source-generated context would provide. Attaching a type-info modifier
    // adds no new reflection beyond what this serializer already performs.
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "MassTransit interop already requires reflection-based JSON; see the AOT guide.")]
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "MassTransit interop already requires reflection-based JSON; see the AOT guide.")]
    private void applyMessageDataOptions()
    {
        if (_messageDataStore is null)
        {
            return;
        }

        var options = SystemTextJsonSerializer.DefaultOptions();
        _jsonConfiguration?.Invoke(options);

        var resolver = options.TypeInfoResolver as DefaultJsonTypeInfoResolver ?? new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(MassTransitMessageDataResolver.ModifierFor(_messageDataStore, _messageDataAddressToId));
        options.TypeInfoResolver = resolver;

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
        var message = new MassTransitEnvelope<object>(envelope)
        {
            DestinationAddress = _destination,
            ResponseAddress = _reply.Value
        };

        return _inner.WriteMessage(message);
    }

    public object ReadFromData(Type messageType, Envelope envelope)
    {
        var wrappedType = typeof(MassTransitEnvelope<>).MakeGenericType(messageType);

        var mtEnvelope = (MassTransitEnvelope)_inner.ReadFromData(wrappedType, envelope);
        mtEnvelope.TransferData(envelope);
        envelope.ReplyUri = mapResponseUri(mtEnvelope.ResponseAddress ?? mtEnvelope.SourceAddress);

        if (_tenantIdSource != null)
        {
            var tenantId = _tenantIdSource(mtEnvelope);
            if (tenantId.IsNotEmpty())
            {
                envelope.TenantId = tenantId;
            }
        }

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