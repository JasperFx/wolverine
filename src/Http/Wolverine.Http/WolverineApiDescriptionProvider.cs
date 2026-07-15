using JasperFx.Core.Reflection;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace Wolverine.Http;

internal class WolverineApiDescriptionProvider : IApiDescriptionProvider
{
    private readonly WolverineHttpOptions _options;
    private readonly ILogger<WolverineApiDescriptionProvider> _logger;

    public WolverineApiDescriptionProvider(WolverineHttpOptions options,
        ILogger<WolverineApiDescriptionProvider> logger)
    {
        _options = options;
        _logger = logger;
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

        publishHostEndpoints();

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

    /// <summary>
    /// Making Wolverine's own descriptions start-independent (GH-3373) is only half an answer on a
    /// hybrid host: the minimal API and MVC providers that run after this one read the host's endpoints
    /// through an EndpointDataSource that ASP.NET Core does not fill until the server starts. Left alone,
    /// a pre-start read caches a document carrying every Wolverine route and none of theirs — which reads
    /// as correct and is not. Publish the host's endpoints first so that whatever is described here is the
    /// whole route table. See GH-3421.
    /// </summary>
    private void publishHostEndpoints()
    {
        if (_options.RouteBuilder is not { } routeBuilder)
        {
            return;
        }

        // Only ever publish from the application's root route builder. MapWolverineEndpoints() can also be
        // called on a route group, whose DataSources are the group's *inner* sources — ASP.NET Core already
        // publishes those on the group's behalf, prefixed and carrying the group's conventions. Publishing
        // them again here would register every endpoint in the group a second time, un-prefixed and stripped
        // of conventions like the group's RequireAuthorization(), at a URL the application never mapped.
        if (HostEndpointDataSources.IsRoot(routeBuilder) && HostEndpointDataSources.TryPublish(routeBuilder))
        {
            return;
        }

        // The host has started, so ASP.NET Core has published its own endpoints and this read is complete
        // whatever Wolverine did or did not do above.
        if (HostEndpointDataSources.AnyPublished(routeBuilder.ServiceProvider))
        {
            return;
        }

        // Left: a pre-start read that Wolverine cannot complete — Wolverine's endpoints were mapped into a
        // route group, or a future ASP.NET Core moved RouteOptions.EndpointDataSources. ASP.NET Core caches
        // this read for the lifetime of the host, so say out loud that what it cached is partial. Discovering
        // that downstream — in a client team that cannot find an endpoint, or a generated SDK silently
        // missing one — costs far more than a warning here.
        _logger.LogWarning(
            "Wolverine could not publish this application's endpoints to ASP.NET Core's ApiExplorer, which " +
            "was read before the host started. ASP.NET Core caches that first read for the lifetime of the " +
            "host, and it describes Wolverine's HTTP endpoints while omitting any minimal API or MVC endpoint " +
            "in this application. Either read the ApiExplorer (OpenAPI/Swagger document generation) after " +
            "IHost.StartAsync(), or call MapWolverineEndpoints() on the WebApplication itself rather than on " +
            "a route group, which is the one shape Wolverine cannot publish from.");
    }

    public void OnProvidersExecuted(ApiDescriptionProviderContext context)
    {
    }

    // Ahead of ASP.NET Core's own providers (EndpointMetadataApiDescriptionProvider at -1100, MVC's
    // DefaultApiDescriptionProvider at -1000), so publishHostEndpoints() lands before they read.
    public int Order => -2000;
}
