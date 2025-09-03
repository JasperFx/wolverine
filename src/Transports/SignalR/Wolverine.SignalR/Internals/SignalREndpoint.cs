using System.Text.Json;
using JasperFx.Core.Reflection;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.SignalR.Internals;

public abstract class SignalREndpoint : Endpoint
{
    private readonly Type _hubType;
    private readonly SignalRTransport _parent;
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
    
    public JsonSerializerOptions JsonOptions { get; set; }

    public static Uri GetUriFromHubType(Type hubType)
    {
        return new Uri($"{SignalRTransport.ProtocolName}://{hubType.NameInCode()}");
    }
    
    internal WolverineHub? Hub { get; set; }
}

public class SignalREndpoint<T> : SignalREndpoint where T : WolverineHub
{
    public SignalREndpoint(SignalRTransport parent) : base(typeof(T), parent)
    {
    }


    public override ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver)
    {
        // Just make sure this exists
        var context = runtime.Services.GetRequiredService<IHubContext<T>>();

        if (Hub == null)
        {
            throw new InvalidOperationException(
                $"WolverineHub {typeof(T).FullNameInCode()} has not been initialized before being accessed");
        }
        
        Hub.Receiver = receiver;
        return new ValueTask<IListener>(Hub);
    }

    protected override ISender CreateSender(IWolverineRuntime runtime)
    {
        // Just make sure this exists
        var context = runtime.Services.GetRequiredService<IHubContext<T>>();

        if (Hub == null)
        {
            throw new InvalidOperationException(
                $"WolverineHub {typeof(T).FullNameInCode()} has not been initialized before being accessed");
        }

        return new SignalRSender<T>(this, context, BuildCloudEventsMapper(runtime, JsonOptions));
    }
    
    public override bool ShouldEnforceBackPressure() => false;

    protected override bool supportsMode(EndpointMode mode)
    {
        return mode != EndpointMode.Durable;
    }
}