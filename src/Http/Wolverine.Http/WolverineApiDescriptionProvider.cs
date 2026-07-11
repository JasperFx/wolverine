using JasperFx.Core.Reflection;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Routing;

namespace Wolverine.Http;

internal class WolverineApiDescriptionProvider : IApiDescriptionProvider
{
    private readonly WolverineHttpOptions _options;

    public WolverineApiDescriptionProvider(WolverineHttpOptions options)
    {
        _options = options;
    }

    public void OnProvidersExecuting(ApiDescriptionProviderContext context)
    {
        // Read the HttpGraph: it is complete as soon as MapWolverineEndpoints() has run,
        // while the composite EndpointDataSource only fills at server start — and ASP.NET
        // Core caches the first ApiExplorer read for the host lifetime, however early it is.
        if (_options.Endpoints is not { } graph)
        {
            return;
        }

        foreach (var chain in graph.Chains)
        {
            // A chain added to the graph after MapWolverineEndpoints() has run never gets a
            // built RouteEndpoint, and CreateApiDescription() requires one
            if (chain.Endpoint == null)
            {
                continue;
            }

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

    public void OnProvidersExecuted(ApiDescriptionProviderContext context)
    {
    }

    public int Order => -2000; // Get this before MVC
}
