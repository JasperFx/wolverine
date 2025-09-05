using JasperFx.Core.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.SignalR.Internals;

namespace Wolverine.SignalR;

public static class SignalRWolverineExtensions
{
    ///     Quick access to the Rabbit MQ Transport within this application.
    ///     This is for advanced usage
    /// </summary>
    /// <param name="endpoints"></param>
    /// <returns></returns>
    internal static SignalRTransport SignalRTransport(this WolverineOptions endpoints, BrokerName? name = null)
    {
        var transports = endpoints.As<WolverineOptions>().Transports;

        return transports.GetOrCreate<SignalRTransport>(name);
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

    public static SignalRListenerConfiguration UseSignalR(this WolverineOptions options)
    {
        var transport = options.SignalRTransport();

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
    public static void MapWolverineSignalRHub(this IEndpointRouteBuilder endpoints, string route = "messages")
    {
        endpoints.MapHub<WolverineHub>(route);
    }

    /// <summary>
    /// Create a subscription rule that publishes matching messages to the SignalR Hub of type "T"
    /// </summary>
    /// <param name="publishing"></param>
    /// <typeparam name="T"></typeparam>
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