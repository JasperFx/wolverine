using JasperFx.Core.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.SignalR.Internals;

namespace Wolverine.SignalR;

public static class SignalRWolverineExtensions
{
    /// <summary>
    /// Quick access to the SignalR Transport within this application.
    /// This is for advanced usage
    /// </summary>
    /// <param name="endpoints"></param>
    /// <returns></returns>
    internal static SignalRTransport SignalRTransport<THub>(this WolverineOptions endpoints, BrokerName? name = null) where THub : WolverineHub
    {
        var transports = endpoints.As<WolverineOptions>().Transports;

        var transport = transports.GetOrCreate<SignalRTransport>(name);
        transport.HubType = typeof(THub);
        return transport;
    }
    
    /// <summary>
    /// Send this message back to the SignalR connection where the current message
    /// was received from
    /// </summary>
    /// <param name="Message"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static ResponseToCallingWebSocket<T> RespondToCallingWebSocket<T>(this T Message) => new(Message);

    /// <summary>
    /// Send this message to the specified group of connections on the SignalR hub
    /// where the current message was received
    /// </summary>
    /// <param name="Message"></param>
    /// <param name="GroupName"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static SignalRMessage<T> ToWebSocketGroup<T>(this T Message, string GroupName) =>
        new(Message, new WebSocketRouting.Group(GroupName));

    /// <summary>
    /// Adds the WolverineHub to this application for SignalR message processing
    /// </summary>
    /// <param name="options"></param>
    /// <param name="configure">Optionally configure the SignalR HubOptions for Wolverine</param>
    /// <returns></returns>
    public static SignalRListenerConfiguration UseSignalR(this WolverineOptions options, Action<HubOptions>? configure = null)
        => options.UseSignalR<WolverineHub>(configure);

    /// <summary>
    /// Adds the WolverineHub to this application for SignalR message processing
    /// </summary>
    /// <param name="options"></param>
    /// <param name="configure">Optionally configure the SignalR HubOptions for Wolverine</param>
    /// <returns></returns>
    public static SignalRListenerConfiguration UseSignalR<THub>(this WolverineOptions options, Action<HubOptions>? configure = null) where THub : WolverineHub
    {
        if (configure == null)
        {
            options.Services.AddSignalR();
        }
        else
        {
            options.Services.AddSignalR(configure);
        }
        
        
        var transport = options.SignalRTransport<THub>();

        options.Services.AddSingleton<SignalRTransport>(s =>
            s.GetRequiredService<IWolverineRuntime>().Options.Transports.GetOrCreate<SignalRTransport>());

        return new SignalRListenerConfiguration(transport);
    }

    /// <summary>
    /// Adds the WolverineHub to this application for Azure SignalR message processing
    /// </summary>
    /// <param name="options"></param>
    /// <param name="configureHub">Optionally configure the SignalR HubOptions for Wolverine</param>
    /// <param name="configureSignalR">Optionally configure the Azure SignalR options for Wolverine</param>
    /// <returns></returns>
    public static SignalRListenerConfiguration UseAzureSignalR(this WolverineOptions options, Action<HubOptions>? configureHub = null, Action<Microsoft.Azure.SignalR.ServiceOptions>? configureSignalR = null)
        => options.UseAzureSignalR<WolverineHub>(configureHub, configureSignalR);

    /// <summary>
    /// Adds a custom Wolverine SignalR hub to this application for Azure SignalR message processing
    /// </summary>
    /// <param name="options"></param>
    /// <param name="configureHub">Optionally configure the SignalR HubOptions for Wolverine</param>
    /// <param name="configureSignalR">Optionally configure the Azure SignalR options for Wolverine</param>
    /// <returns></returns>
    public static SignalRListenerConfiguration UseAzureSignalR<THub>(this WolverineOptions options, Action<HubOptions>? configureHub = null, Action<Microsoft.Azure.SignalR.ServiceOptions>? configureSignalR = null) where THub : WolverineHub
    {
        configureHub ??= _ => { };
        configureSignalR ??= _ => { };

        options.Services.AddSignalR(configureHub).AddAzureSignalR(configureSignalR);

        var transport = options.SignalRTransport<THub>();

        options.Services.AddSingleton<SignalRTransport>(s =>
            s.GetRequiredService<IWolverineRuntime>().Options.Transports.GetOrCreate<SignalRTransport>());

        return new SignalRListenerConfiguration(transport);
    }

    /// <summary>
    /// Syntactical shortcut to register the WolverineHub SignalR Hub for sending
    /// messages to this server. Equivalent to MapHub<WolverineHub>(route).
    /// </summary>
    /// <param name="endpoints"></param>
    /// <param name="route"></param>
    public static HubEndpointConventionBuilder MapWolverineSignalRHub(this IEndpointRouteBuilder endpoints, string route = "messages")
        => endpoints.MapWolverineSignalRHub<WolverineHub>(route);

    /// <summary>
    /// Syntactical shortcut to register a custom Wolverine SignalR Hub for sending
    /// messages to this server. Equivalent to MapHub<THub>(route).
    /// </summary>
    /// <param name="endpoints"></param>
    /// <param name="route"></param>
    public static HubEndpointConventionBuilder MapWolverineSignalRHub<THub>(this IEndpointRouteBuilder endpoints, string route = "messages") where THub : WolverineHub
    {
        return endpoints.MapHub<THub>(route);
    }

    /// <summary>
    /// Create a subscription rule that publishes matching messages to the default Wolverine SignalR Hub
    /// </summary>
    /// <param name="publishing"></param>
    /// <returns></returns>
    public static SignalRSubscriberConfiguration ToSignalR(this IPublishToExpression publishing)
    {
        var transports = publishing.As<PublishingExpression>().Parent.Transports;
        var transport = transports.GetOrCreate<SignalRTransport>();

        // This is necessary unfortunately to hook up the subscription rules
        publishing.To(transport.Uri);

        return new SignalRSubscriberConfiguration(transport);
    }
}