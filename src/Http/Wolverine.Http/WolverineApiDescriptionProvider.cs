using JasperFx.Core.Reflection;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;

namespace Wolverine.Http;

internal class WolverineApiDescriptionProvider : IApiDescriptionProvider
{
    private readonly EndpointDataSource _endpointDataSource;
    private readonly IHostEnvironment _environment;

    public WolverineApiDescriptionProvider(
        EndpointDataSource endpointDataSource,
        IHostEnvironment environment)
    {
        _endpointDataSource = endpointDataSource;
        _environment = environment;
    }

    public void OnProvidersExecuting(ApiDescriptionProviderContext context)
    {
        foreach (var endpoint in _endpointDataSource.Endpoints)
        {
            if (endpoint is RouteEndpoint routeEndpoint && routeEndpoint.Metadata.GetMetadata<HttpChain>() is {} chain)
            {
                if (chain.Method.HandlerType.HasAttribute<ExcludeFromDescriptionAttribute>() ||
                    chain.Method.Method.HasAttribute<ExcludeFromDescriptionAttribute>())
                {
                    continue;
                }

                foreach (var httpMethod in chain.HttpMethods)
                {
                    // OpenAPI 3.1 (and the Swashbuckle / Microsoft.OpenApi stack Wolverine emits with)
                    // has no representation for the QUERY verb (RFC 10008) — Swashbuckle throws a
                    // KeyNotFoundException mapping the method, which would break document generation for
                    // the whole app. Gracefully omit QUERY endpoints from the API description, matching
                    // ASP.NET Core's own behavior on OpenAPI 3.1. QUERY becomes a first-class operation
                    // in OpenAPI 3.2; first-class documentation can follow once the stack supports it.
                    if (string.Equals(httpMethod, "QUERY", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    context.Results.Add(chain.CreateApiDescription(httpMethod));
                }
            }
        }
    }

    public void OnProvidersExecuted(ApiDescriptionProviderContext context)
    {
    }

    public int Order => -2000; // Get this before MVC
}