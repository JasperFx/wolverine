using System.Reflection;
using JasperFx.Core.Reflection;
using Microsoft.AspNetCore.Mvc.Abstractions;
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