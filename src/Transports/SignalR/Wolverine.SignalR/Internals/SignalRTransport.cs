using System.Text.Json;
using System.Text.Json.Serialization;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Runtime.Interop;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.SignalR.Internals;

public class SignalRTransport : Endpoint, ITransport, IListener, ISender
{
    private CloudEventsMapper? _mapper;
    public static readonly string ProtocolName = "signalr";
    public static readonly string DefaultOperation = "ReceiveMessage";

    public SignalRTransport() : base($"{ProtocolName}://wolverine".ToUri(), EndpointRole.Application)
    {
        IsListener = true;

        JsonOptions = new(JsonSerializerOptions.Web) { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        JsonOptions.Converters.Add(new JsonStringEnumConverter());
    }

    protected override ISender CreateSender(IWolverineRuntime runtime)
    {
        return this;
    }

    public string Protocol => ProtocolName;
    public string Name => "Wolverine SignalR Transport";

    Endpoint? ITransport.ReplyEndpoint() => this;

    Endpoint ITransport.GetOrCreateEndpoint(Uri uri) => this;

    Endpoint? ITransport.TryGetEndpoint(Uri uri) => this;

    IEnumerable<Endpoint> ITransport.Endpoints()
    {
        yield return this;
    }

    ValueTask ITransport.InitializeAsync(IWolverineRuntime runtime)
    {
        Compile(runtime);
        
        _mapper ??= BuildCloudEventsMapper(runtime, JsonOptions);
        Logger ??= runtime.LoggerFactory.CreateLogger<SignalRTransport>();
        HubContext ??= runtime.Services.GetRequiredService<IHubContext<WolverineHub>>();

        return new ValueTask();
    }

    public IHubContext<WolverineHub>? HubContext { get; private set; }

    bool ITransport.TryBuildStatefulResource(IWolverineRuntime runtime, out IStatefulResource? resource)
    {
        resource = null;
        return false;
    }

    internal ILogger<SignalRTransport>? Logger { get; set; }

    public JsonSerializerOptions JsonOptions { get; set; }

    public IReceiver? Receiver { get; private set; }
    
    internal async Task ReceiveAsync(HubCallerContext context, string json)
    {
        try
        {
            if (Receiver == null || _mapper == null)
            {
                throw new InvalidOperationException(
                    "The SignalR Transport has not been initialized. Ensure that there is a WolverineOptions.UseSignalR() call in your configuration");
            }

            
            var envelope = new SignalREnvelope(context, HubContext!);
            _mapper!.MapIncoming(envelope, json);
            await Receiver.ReceivedAsync(this, envelope);
        }
        catch (Exception e)
        {
            Logger?.LogError(e, "Error while receiving CloudEvents message from SignalR");
        }
    }

    public override ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver)
    {
        Receiver = receiver;
        
        return new ValueTask<IListener>(this);
    }

    public IHandlerPipeline? Pipeline => Receiver?.Pipeline;

    ValueTask IChannelCallback.CompleteAsync(Envelope envelope)
    {
        return new ValueTask();
    }

    ValueTask IChannelCallback.DeferAsync(Envelope envelope)
    {
        return new ValueTask();
    }

    public ValueTask DisposeAsync()
    {
        return new ValueTask();
    }

    public Uri Address => Uri;

    ValueTask IListener.StopAsync()
    {
        return new ValueTask();
    }
    
    protected override bool supportsMode(EndpointMode mode)
    {
        return mode != EndpointMode.Durable;
    }
    
    public override bool ShouldEnforceBackPressure() => false;
    
    public bool SupportsNativeScheduledSend => false;
    public Uri Destination => Uri;
    public async Task<bool> PingAsync()
    {
        try
        {
            await HubContext!.Clients.All.SendAsync("ping");
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public ValueTask SendAsync(Envelope envelope)
    {
        // This is controlling which subset of active connections
        // should get the message
        var locator = WebSocketRouting.DetermineLocator(envelope);
        
        // DefaultOperation = "ReceiveMessage" in this case
        // Wolverine users will be able to opt into sending messages to different SignalR
        // operations on the client
        var operation = envelope.TopicName ?? SignalRTransport.DefaultOperation;

        var json = _mapper!.WriteToString(envelope);

        return new ValueTask(locator.Find(HubContext!).SendAsync(operation, json));
    }
}
