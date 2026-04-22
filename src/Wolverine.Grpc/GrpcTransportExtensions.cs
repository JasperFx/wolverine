using JasperFx.Core.Reflection;
using Wolverine.Configuration;

namespace Wolverine.Grpc;

public static class GrpcTransportExtensions
{
    /// <summary>
    ///     Configure Wolverine to listen for gRPC messages on the specified port.
    ///     The listener starts an embedded gRPC server bound to all interfaces.
    /// </summary>
    public static GrpcListenerConfiguration ListenAtGrpcPort(this WolverineOptions options, int port)
    {
        var transport = options.Transports.GetOrCreate<GrpcTransport>();
        var endpoint = transport.EndpointForLocalPort(port);
        endpoint.IsListener = true;
        return new GrpcListenerConfiguration(endpoint);
    }

    /// <summary>
    ///     Publish messages to the specified gRPC endpoint (host:port).
    /// </summary>
    public static GrpcSubscriberConfiguration ToGrpcEndpoint(
        this IPublishToExpression publishing,
        string host,
        int port)
    {
        var transports = publishing.As<PublishingExpression>().Parent.Transports;
        var transport = transports.GetOrCreate<GrpcTransport>();
        var endpoint = transport.EndpointFor(host, port);
        publishing.To(endpoint.Uri);
        return new GrpcSubscriberConfiguration(endpoint);
    }
}
