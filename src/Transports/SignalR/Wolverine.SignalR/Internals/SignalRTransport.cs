using System.Text.Json;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Wolverine.Transports;

namespace Wolverine.SignalR.Internals;

public class SignalRTransport : TransportBase<SignalREndpoint>
{
    public static readonly string ProtocolName = "signalr";
    public static readonly string DefaultOperation = "ReceiveMessage";
    public Cache<Type, SignalREndpoint> HubEndpoints { get; }
    
    public SignalRTransport() : base(ProtocolName, "SignalR Messaging Integration")
    {
        HubEndpoints = new(hubType => typeof(SignalREndpoint<>).CloseAndBuildAs<SignalREndpoint>(hubType));
    }

    protected override IEnumerable<SignalREndpoint> endpoints()
    {
        return HubEndpoints;
    }

    protected override SignalREndpoint findEndpointByUri(Uri uri)
    {
        return HubEndpoints.Single(x => x.Uri == uri);
    }

    public JsonSerializerOptions JsonOptions { get; } = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
}