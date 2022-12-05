using JasperFx.Core.Reflection;
using Wolverine.Configuration;

namespace Wolverine.Transports.Tcp;

public static class TcpTransportConfigurationExtensions
{
    /// <summary>
    ///     Directs the application to listen at the designated port in a
    ///     fast, but non-durable way
    /// </summary>
    /// <param name="port"></param>
    public static IListenerConfiguration ListenAtPort(this WolverineOptions options, int port)
    {
        var endpoint = options.Transports.GetOrCreate<TcpTransport>().GetOrCreateEndpoint(TcpEndpoint.ToUri(port));
        endpoint.IsListener = true;
        return new ListenerConfiguration(endpoint);
    }

    /// <summary>
    ///     Publish the designated message types using Wolverine's lightweight
    ///     TCP transport locally to the designated port number
    /// </summary>
    /// <param name="port"></param>
    public static ISubscriberConfiguration ToPort(this IPublishToExpression publishing, int port)
    {
        publishing.As<PublishingExpression>().Parent.Transports.GetOrCreate<TcpTransport>();
        var uri = TcpEndpoint.ToUri(port);
        return publishing.To(uri);
    }

    /// <summary>
    ///     Publish messages using the TCP transport to the specified
    ///     server name and port
    /// </summary>
    /// <param name="hostName"></param>
    /// <param name="port"></param>
    public static ISubscriberConfiguration ToServerAndPort(this IPublishToExpression publishing, string hostName,
        int port)
    {
        publishing.As<PublishingExpression>().Parent.Transports.GetOrCreate<TcpTransport>();
        var uri = TcpEndpoint.ToUri(port, hostName);
        return publishing.To(uri);
    }
}