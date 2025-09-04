using System.Text.Json;
using JasperFx.Core.Reflection;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Runtime.Interop;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.SignalR.Internals;

public abstract class SignalREndpoint : Endpoint, IListener
{
    private readonly Type _hubType;
    private readonly SignalRTransport _parent;
    protected CloudEventsMapper? _mapper;
    
    public Type HubType => _hubType;
    
    public SignalREndpoint(Type hubType, SignalRTransport parent) : base(GetUriFromHubType(hubType), EndpointRole.Application)
    {
        if (!hubType.CanBeCastTo<WolverineHub>())
        {
            throw new ArgumentOutOfRangeException(nameof(hubType),
                $"{hubType.FullNameInCode()} does not inherit from {typeof(WolverineHub).FullNameInCode()}");
        }

        _hubType = hubType;
        _parent = parent;

        JsonOptions = parent.JsonOptions;
    }
    
    internal ILogger<SignalREndpoint>? Logger { get; set; }
    
    public JsonSerializerOptions JsonOptions { get; set; }

    public static Uri GetUriFromHubType(Type hubType)
    {
        return new Uri($"{SignalRTransport.ProtocolName}://{hubType.NameInCode()}");
    }

    public IReceiver? Receiver { get; private set; }
    
    internal async Task ReceiveAsync(HubCallerContext context, WolverineHub wolverineHub, string json)
    {
        if (Receiver == null || _mapper == null) return;
        
        try
        {
            var envelope = new SignalREnvelope(context, wolverineHub);
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
        Compile(runtime);
        
        _mapper ??= BuildCloudEventsMapper(runtime, JsonOptions);
        Logger ??= runtime.LoggerFactory.CreateLogger<SignalREndpoint>();

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
}

public class SignalREndpoint<T> : SignalREndpoint where T : WolverineHub
{
    public SignalREndpoint(SignalRTransport parent) : base(typeof(T), parent)
    {
    }
    
    protected override ISender CreateSender(IWolverineRuntime runtime)
    {
        Compile(runtime);

        _mapper ??= BuildCloudEventsMapper(runtime, JsonOptions);
        Logger ??= runtime.LoggerFactory.CreateLogger<SignalREndpoint>();
        
        // Just make sure this exists
        var context = runtime.Services.GetRequiredService<IHubContext<T>>();

        return new SignalRSender<T>(this, context, BuildCloudEventsMapper(runtime, JsonOptions));
    }
    
    public override bool ShouldEnforceBackPressure() => false;

    protected override bool supportsMode(EndpointMode mode)
    {
        return mode != EndpointMode.Durable;
    }
}