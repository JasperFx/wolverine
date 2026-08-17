using System.Text.Json;
using System.Text.Json.Serialization;
using JasperFx.Core;
using JasperFx.Descriptors;
using JasperFx.Resources;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Configuration.Capabilities;
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

    /// <summary>
    ///     GH-3972. The client operation coalesced batches are sent on. Deliberately distinct from
    ///     <see cref="DefaultOperation" />: a client that does not know about coalescing then never receives
    ///     these at all, instead of receiving something on ReceiveMessage that it tries to read as a single
    ///     CloudEvents document and fails on per message.
    /// </summary>
    public static readonly string CoalescedOperation = "ReceiveCoalescedMessages";

    public SignalRTransport() : base($"{ProtocolName}://wolverine".ToUri(), EndpointRole.Application)
    {
        IsListener = true;
        BrokerRole = "hub";

        #region sample_signalr_default_json_configuration
        JsonOptions = new(JsonSerializerOptions.Web) { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        JsonOptions.Converters.Add(new JsonStringEnumConverter());

        #endregion
    }
    
    public virtual bool TryBuildBrokerUsage(out BrokerDescription description)
    {
        description = new BrokerDescription(this);
        return true;
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

        var hubContextType = typeof(IHubContext<>).MakeGenericType(HubType);
        HubContext ??= (IHubContext<Hub>)runtime.Services.GetRequiredService(hubContextType);

        if (Coalescing != null)
        {
            Coalescer ??= new OutgoingCoalescer(Coalescing, HubContext!, JsonOptions, Logger);
        }

        return new ValueTask();
    }

    [IgnoreDescription]
    public IHubContext<Hub>? HubContext { get; private set; }
    public Type HubType { get; internal set; } = typeof(WolverineHub);

    bool ITransport.TryBuildStatefulResource(IWolverineRuntime runtime, out IStatefulResource? resource)
    {
        resource = null;
        return false;
    }

    internal ILogger<SignalRTransport>? Logger { get; set; }

    [IgnoreDescription]
    public JsonSerializerOptions JsonOptions { get; set; }

    /// <summary>
    ///     GH-3972. When set, outgoing messages are accumulated per destination and flushed as a single
    ///     envelope. Null (the default) sends each message immediately.
    /// </summary>
    [IgnoreDescription]
    internal OutgoingCoalescingOptions? Coalescing { get; set; }

    [IgnoreDescription]
    internal OutgoingCoalescer? Coalescer { get; private set; }

    [IgnoreDescription]
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

            if (Logger?.IsEnabled(LogLevel.Debug) ?? false)
            {
                Logger.LogDebug("Received JSON from SignalR: {Json} ", json);   
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

    [IgnoreDescription]
    public IHandlerPipeline? Pipeline => Receiver?.Pipeline;

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
        // GH-3972: drains any buffered outgoing messages so ones enqueued just before stop are not dropped
        if (Coalescer != null)
        {
            await Coalescer.DisposeAsync();
            Coalescer = null;
        }
    }

    public Uri Address => Uri;

    async ValueTask IListener.StopAsync()
    {
        if (Coalescer != null)
        {
            await Coalescer.DrainAsync();
        }
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

        if (Logger != null && Logger.IsEnabled(LogLevel.Debug))
        {
            Logger.LogDebug("Sent JSON via SignalR: {Json}", json);
        }

        // GH-3972: when coalescing is on, buffer per destination rather than sending immediately
        if (Coalescer is { } coalescer)
        {
            return coalescer.EnqueueAsync(locator, operation, json);
        }

        return new ValueTask(locator.Find(HubContext!).SendAsync(operation, json));
    }
}
