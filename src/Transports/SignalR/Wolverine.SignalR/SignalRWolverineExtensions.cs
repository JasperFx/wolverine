using JasperFx.Core.Reflection;
using Wolverine.Configuration;
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
    public static ResponseToCaller<T> RespondToCaller<T>(this T Message) => new(Message);

    /// <summary>
    /// Send this message to the specified group of connections on the SignalR hub
    /// where the current message was received
    /// </summary>
    /// <param name="Message"></param>
    /// <param name="GroupName"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static SignalRMessage<T> SendToGroup<T>(this T Message, string GroupName) =>
        new(Message, new WebSocketRouting.Group(GroupName));

    public static SignalRListenerConfiguration UseSignalR<T>(this WolverineOptions options) where T : WolverineHub
    {
        var transport = options.SignalRTransport();
        var endpoint = transport.HubEndpoints[typeof(T)];

        return new SignalRListenerConfiguration(endpoint);
    }

    public static SignalRSubscriberConfiguration ToSignalR<T>(this IPublishToExpression publishing) where T : WolverineHub
    {
        var transports = publishing.As<PublishingExpression>().Parent.Transports;
        var transport = transports.GetOrCreate<SignalRTransport>();

        var endpoint = transport.HubEndpoints[typeof(T)];
        
        // This is necessary unfortunately to hook up the subscription rules
        publishing.To(endpoint.Uri);

        return new SignalRSubscriberConfiguration(endpoint);
    }
}