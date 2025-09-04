using System.Diagnostics;
using System.Text.Json;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.AspNetCore.SignalR.Client;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Runtime.Interop;
using Wolverine.SignalR.Internals;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.SignalR.Client;

public static class SignalRClientExtensions
{
    public static Uri UseSignalRClient(this WolverineOptions options, string url,
        JsonSerializerOptions? jsonOptions = null)
    {
        var transport = options.Transports.GetOrCreate<SignalRClientTransport>();
        
        var endpoint = transport.ForClientUrl(url);
        if (jsonOptions != null)
        {
            endpoint.JsonOptions = jsonOptions;
        }

        return endpoint.Uri;
    }

    /// <summary>
    /// Send a message via a SignalRClient for the given server Uri
    /// </summary>
    /// <param name="bus"></param>
    /// <param name="serverUri"></param>
    /// <param name="message"></param>
    /// <returns></returns>
    public static ValueTask SendViaSignalRClient(this IMessageBus bus, Uri serverUri, object message)
    {
        var wolverineUri = SignalRClientEndpoint.TranslateToWolverineUri(serverUri);
        return bus.EndpointFor(wolverineUri).SendAsync(message);
    }
    
    /// <summary>
    /// Send a message via a SignalRClient for the given server Uri
    /// </summary>
    /// <param name="bus"></param>
    /// <param name="serverUri"></param>
    /// <param name="message"></param>
    /// <returns></returns>
    public static ValueTask SendViaSignalRClient(this IMessageBus bus, string serverUrl, object message)
    {
        var wolverineUri = SignalRClientEndpoint.TranslateToWolverineUri(new Uri(serverUrl));
        return bus.EndpointFor(wolverineUri).SendAsync(message);
    }

    /// <summary>
    /// Route messages via a SignalR Client pointed at the localhost, port, and relativeUrl
    /// </summary>
    /// <param name="publishing"></param>
    /// <param name="port"></param>
    /// <param name="relativeUrl"></param>
    public static void ToSignalRWithClient(this IPublishToExpression publishing, int port, string relativeUrl)
    {
        var url = $"http://localhost:{port}/{relativeUrl}";
        publishing.ToSignalRWithClient(url);
    }
    
    /// <summary>
    /// Route messages via a SignalR Client pointed at the supplied absolute Url
    /// </summary>
    /// <param name="publishing"></param>
    /// <param name="url"></param>
    public static void ToSignalRWithClient(this IPublishToExpression publishing, string url)
    {
        var rawUri = new Uri(url);
        if (!rawUri.IsAbsoluteUri)
        {
            throw new ArgumentOutOfRangeException(nameof(url), "Must be an absolute Url");
        }
        
        var uri = SignalRClientEndpoint.TranslateToWolverineUri(rawUri);
        var transports = publishing.As<PublishingExpression>().Parent.Transports;
        var transport = transports.GetOrCreate<SignalRClientTransport>();

        var endpoint = transport.Clients[uri];
        publishing.To(uri);
    }
    
    
}

public class SignalRClientTransport : TransportBase<SignalRClientEndpoint>
{
    public static readonly string ProtocolName = "signalr-client";
    
    public Cache<Uri, SignalRClientEndpoint> Clients { get; }

    public SignalRClientTransport() : base(ProtocolName, "SignalR Client")
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

public class SignalRClientEndpoint : Endpoint, IListener, ISender
{
    private readonly SignalRClientTransport _parent;
    private HubConnection? _connection;
    private CloudEventsMapper? _mapper;

    internal static Uri TranslateToWolverineUri(Uri uri)
    {
        return new Uri($"{SignalRClientTransport.ProtocolName}://{uri.Host}:{uri.Port}/{uri.Segments.Last()}");
    }
    
    public SignalRClientEndpoint(Uri uri, SignalRClientTransport parent) : base(TranslateToWolverineUri(uri),EndpointRole.Application)
    {
        _parent = parent;
        SignalRUri = uri;

        IsListener = true;

        Mode = EndpointMode.Inline;
    }

    public JsonSerializerOptions JsonOptions { get; set; } = new JsonSerializerOptions
        { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public Uri SignalRUri { get; }

    public override async ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver)
    {
        Receiver = receiver;
        Pipeline = runtime.Pipeline;
        _connection ??= new HubConnectionBuilder().WithAutomaticReconnect().WithUrl(SignalRUri).Build();
        _mapper ??= BuildCloudEventsMapper(runtime, JsonOptions);
        
        await _connection.StartAsync();

        _connection.On(SignalRTransport.DefaultOperation, [typeof(string)], (args =>
        {
            var json = args[0] as string;
            
            // TODO -- log if the JSON was null or empty
            return json == null ? Task.CompletedTask : ReceiveAsync(json);
        }));

        return this;
    }
    
    internal async Task ReceiveAsync(string json)
    {
        if (Receiver == null || _mapper == null) return;

        // TODO -- MUCH MORE ERROR HANDLING!!!!
        var envelope = new Envelope();
        _mapper!.MapIncoming(envelope, json);
        await Receiver.ReceivedAsync(this, envelope);
    }
    
    public IReceiver? Receiver { get; private set; }

    protected override ISender CreateSender(IWolverineRuntime runtime)
    {
        _connection ??= new HubConnectionBuilder().WithUrl(SignalRUri).Build();
        _mapper ??= BuildCloudEventsMapper(runtime, JsonOptions);
        return this;
    }

    public IHandlerPipeline? Pipeline { get; private set; }

    ValueTask IChannelCallback.CompleteAsync(Envelope envelope)
    {
        return new ValueTask();
    }

    ValueTask IChannelCallback.DeferAsync(Envelope envelope)
    {
        return new ValueTask();
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection != null)
        {
            await _connection.StopAsync();
            await _connection.DisposeAsync();
        }

        _connection = null;
    }

    Uri IListener.Address => Uri;

    async ValueTask IListener.StopAsync()
    {
        if (_connection != null)
        {
            await _connection.StopAsync();
        }
    }

    bool ISender.SupportsNativeScheduledSend => false;
    Uri ISender.Destination => Uri;
    public Task<bool> PingAsync()
    {
        return Task.FromResult(true);
    }

    public async ValueTask SendAsync(Envelope envelope)
    {
        var json = _mapper.WriteToString(envelope);
        await _connection.InvokeAsync(nameof(WolverineHub.Receive), json);
    }
}