using System.Text.Json;
using Lamar;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Configuration;
using Wolverine.Runtime;

namespace Wolverine.Http;

public class WolverineRequiredException : Exception
{
    public WolverineRequiredException(Exception? innerException) : base("Wolverine is either not added to this application through IHostBuilder.UseWolverine() or is invalid", innerException)
    {
    }
}

public static class WolverineHttpEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Discover and add Wolverine HTTP endpoints to your ASP.Net Core system
    /// </summary>
    /// <param name="endpoints"></param>
    /// <param name="configure"></param>
    /// <exception cref="WolverineRequiredException"></exception>
    public static void MapWolverineEndpoints(this IEndpointRouteBuilder endpoints, Action<WolverineHttpOptions>? configure = null)
    {
        WolverineRuntime runtime;

        // TODO -- unit test this behavior
        try
        {
            runtime = (WolverineRuntime)endpoints.ServiceProvider.GetRequiredService<IWolverineRuntime>();
        }
        catch (Exception e)
        {
            throw new WolverineRequiredException(e);
        }

        var container = (IContainer)endpoints.ServiceProvider;
        
        // Making sure this exists
        var options = container.GetInstance<WolverineHttpOptions>();
        configure?.Invoke(options);
        
        options.JsonSerializerOptions = container.TryGetInstance<JsonOptions>()?.SerializerOptions ?? new JsonSerializerOptions();
        options.Endpoints = new EndpointGraph(runtime.Options, container);
        options.Endpoints.DiscoverEndpoints(options);
        
        container.GetInstance<WolverineSupplementalCodeFiles>().Collections.Add(options.Endpoints);

        endpoints.DataSources.Add(options.Endpoints);
    }
}