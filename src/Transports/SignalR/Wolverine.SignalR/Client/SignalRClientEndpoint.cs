using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Runtime.Interop;
using Wolverine.SignalR.Internals;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.SignalR.Client;

public class SignalRClientEndpoint : Endpoint, IListener, ISender
{
    private readonly SignalRClientTransport _parent;
    private HubConnection? _connection;
    private CloudEventsMapper? _mapper;
    private ILogger<SignalRClientEndpoint>? Logger;

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

        Logger = runtime.LoggerFactory.CreateLogger<SignalRClientEndpoint>();
        
        await _connection.StartAsync();

        _connection.On(SignalRTransport.DefaultOperation, [typeof(string)], (args =>
        {
            var json = args[0] as string;

            if (json is null)
            {
                Logger.LogDebug("Received an empty message, ignoring");
                return Task.CompletedTask;
            }
            
            return ReceiveAsync(json);
        }));

        return this;
    }
    
    internal async Task ReceiveAsync(string json)
    {
        if (Receiver == null || _mapper == null) return;

        try
        {
            var envelope = new Envelope();
            _mapper!.MapIncoming(envelope, json);
            await Receiver.ReceivedAsync(this, envelope);
        }
        catch (Exception e)
        {
            Logger?.LogError(e, "Unable to receive a message from SignalR");
        }
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
        if (_mapper == null || _connection == null)
            throw new InvalidOperationException($"SignalR Client {Uri} is not initialized");
        
        var json = _mapper.WriteToString(envelope);
        await _connection.InvokeAsync(nameof(WolverineHub.Receive), json);
    }
}