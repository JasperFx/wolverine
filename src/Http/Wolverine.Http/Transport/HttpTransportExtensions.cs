using System.Diagnostics;
using System.Text.Json;
using JasperFx.Core.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Wolverine.Configuration;

namespace Wolverine.Http.Transport;

public static class HttpTransportExtensions
{
    /// <summary>
    /// Add Wolverine's HTTP Transport option to your system's AspNetCore routing tree
    /// </summary>
    /// <param name="endpoints"></param>
    /// <param name="groupUrlPrefix"></param>
    /// <returns></returns>
    public static RouteGroupBuilder MapWolverineHttpTransportEndpoints(this IEndpointRouteBuilder endpoints, string groupUrlPrefix = "/_wolverine", JsonSerializerOptions? jsonOptions = null)
    {
        var group = endpoints.MapGroup(groupUrlPrefix);
        
        group.MapPost(
            "/batch/{queue}",(HttpContext c, HttpTransportExecutor executor) => executor.ExecuteBatchAsync(c));

        group.MapPost("/invoke", (HttpContext c, HttpTransportExecutor executor) => executor.InvokeAsync(c, jsonOptions));

        return group;
    }
    
    /// <summary>
    /// Publish message(s) to the specified http endpoint
    /// </summary>
    /// <param name="publishing"></param>
    /// <param name="url"></param>
    /// <param name="supportsNativeScheduledSend"></param>
    /// <param name="useCloudEvents"></param>
    /// <param name="options"></param>
    /// <returns></returns>
    public static HttpTransportSubscriberConfiguration ToHttpEndpoint(
        this IPublishToExpression publishing,
        string url,
        bool supportsNativeScheduledSend = false,
        bool useCloudEvents = false,
        JsonSerializerOptions options = null)
    {
        var transports =
            publishing.As<PublishingExpression>().Parent.Transports;
        var transport = transports.GetOrCreate<HttpTransport>();

        var endpoint = transport.EndpointFor(url);
        if (useCloudEvents)
        {
            endpoint.SerializerOptions = options;
        }

        // This is necessary unfortunately to hook up the subscription rules
        publishing.To(endpoint.Uri);
        endpoint.SupportsNativeScheduledSend = supportsNativeScheduledSend;
        return new HttpTransportSubscriberConfiguration(endpoint);
    }
}