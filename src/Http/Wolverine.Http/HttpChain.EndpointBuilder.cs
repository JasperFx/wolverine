using System.Reflection;
using JasperFx.RuntimeCompiler;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Wolverine.Http.Metadata;

namespace Wolverine.Http;

public partial class HttpChain
{
    private IEnumerable<object> buildMetadata()
    {
        // For diagnostics
        yield return this;

        yield return Method.Method;

        // This is just to let the world know that the endpoint came from Wolverine
        yield return new WolverineMarker();

        // Custom metadata
        foreach (var metadata in Metadata) yield return metadata;

        // TODO -- figure out how to get at the Cors preflight stuff
        yield return new HttpMethodMetadata(_httpMethods);

        if (RequestType != null)
        {
            yield return new WolverineAcceptsMetadata(this);
            yield return new WolverineProducesResponse { StatusCode = 400 };
        }

        if (ResourceType != null)
        {
            yield return new WolverineProducesResponse
            {
                StatusCode = 200,
                Type = ResourceType,
                ContentTypes = new[] { "application/json" }
            };

            yield return new WolverineProducesResponse
            {
                StatusCode = 404
            };
        }
        else
        {
            yield return new WolverineProducesResponse { StatusCode = 200 };
        }

        foreach (var attribute in Method.HandlerType.GetCustomAttributes()) yield return attribute;

        foreach (var attribute in Method.Method.GetCustomAttributes()) yield return attribute;
    }
    
    public RouteEndpoint BuildEndpoint()
    {
        var handler = new Lazy<HttpHandler>(() =>
        {
            this.InitializeSynchronously(_parent.Rules, _parent, _parent.Container);
            return (HttpHandler)_parent.Container.QuickBuild(_handlerType);
        });

        Endpoint = new RouteEndpoint(c => handler.Value.Handle(c), RoutePattern, Order,
            new EndpointMetadataCollection(buildMetadata()), DisplayName);

        return Endpoint;
    }
}