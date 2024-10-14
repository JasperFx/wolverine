using JasperFx.Core;
using JasperFx.Core.Reflection;
using Wolverine.Configuration;
using Wolverine.Util;

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

    /// <summary>
    /// Probably mostly just for testing, but if you need a control endpoint for your
    /// Wolverine application because of Wolverine managed agent assignments and cannot
    /// use any other transport, use a TCP endpoint to an open port for this node
    /// </summary>
    /// <param name="options"></param>
    public static void UseTcpForControlEndpoint(this WolverineOptions options)
    {
        var port = PortFinder.GetAvailablePort();
        var controlUri = $"tcp://localhost:{port}".ToUri();
        var controlPoint = options.Transports.GetOrCreateEndpoint(controlUri);
        options.Transports.NodeControlEndpoint = controlPoint;
    }
}