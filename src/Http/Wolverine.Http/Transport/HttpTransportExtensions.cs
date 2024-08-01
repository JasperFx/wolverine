using System.Diagnostics;
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
    public static RouteGroupBuilder MapWolverineHttpTransportEndpoints(this IEndpointRouteBuilder endpoints, string groupUrlPrefix = "/_wolverine")
    {
        var group = endpoints.MapGroup(groupUrlPrefix);
        
        group.MapPost(
            "/batch/{queue}",(HttpContext c, HttpTransportExecutor executor) => executor.ExecuteBatchAsync(c));

        group.MapPost("/invoke", (HttpContext c, HttpTransportExecutor executor) => executor.InvokeAsync(c));

        return group;
    }
    
    /// <summary>
    /// Publish message(s) to the specified http endpoint
    /// </summary>
    /// <param name="publishing"></param>
    /// <param name="url"></param>
    /// <returns></returns>
    public static HttpTransportSubscriberConfiguration ToHttpEndpoint(this IPublishToExpression publishing, string url)
    {
        var transports = publishing.As<PublishingExpression>().Parent.Transports;
        var transport = transports.GetOrCreate<HttpTransport>();

        var endpoint = transport.EndpointFor(url);

        // This is necessary unfortunately to hook up the subscription rules
        publishing.To(endpoint.Uri);

        return new HttpTransportSubscriberConfiguration(endpoint);
    }
}