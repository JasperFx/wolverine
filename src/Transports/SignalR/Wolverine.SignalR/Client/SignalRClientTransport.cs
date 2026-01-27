using System.Diagnostics;
using JasperFx.Core;
using Wolverine.Transports;

namespace Wolverine.SignalR.Client;

public class SignalRClientTransport : TransportBase<SignalRClientEndpoint>
{
    public static readonly string ProtocolName = "signalr-client";
    
    public Cache<Uri, SignalRClientEndpoint> Clients { get; }

    public SignalRClientTransport() : base(ProtocolName, "SignalR Client", ["signalr"])
    {
        Clients = new Cache<Uri, SignalRClientEndpoint>(uri => new SignalRClientEndpoint(uri, this));
    }

    protected override IEnumerable<SignalRClientEndpoint> endpoints()
    {
        return Clients;
    }

    protected override SignalRClientEndpoint findEndpointByUri(Uri uri)
    {
        return Clients.FirstOrDefault(x => x.Uri == uri);
    }

    public SignalRClientEndpoint ForClientUrl(string clientUrl)
    {
        var wolverineUri = SignalRClientEndpoint.TranslateToWolverineUri(new Uri(clientUrl));

        if (!Clients.TryFind(wolverineUri, out var endpoint))
        {
            endpoint = new SignalRClientEndpoint(new Uri(clientUrl), this);
            Clients[wolverineUri] = endpoint;
        }

        return endpoint;
    }
}